using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.TextRepair;

/// <summary>Outcome of repairing one item.</summary>
public readonly record struct RepairOutcome(bool ItemChanged, int ChaptersFixed, int StreamsFixed, IReadOnlyList<string> Unresolved)
{
    /// <summary>Gets a value indicating whether anything was changed.</summary>
    public bool AnyChange => ItemChanged || ChaptersFixed > 0 || StreamsFixed > 0;
}

/// <summary>
/// Repairs the text fields of an item in the database: double encoding everywhere, and replacement
/// characters in the title when the original title gives the missing letters. Never touches files.
/// </summary>
public class ItemTextRepairer
{
    private readonly ILogger<ItemTextRepairer> _logger;
    private readonly IChapterManager _chapterManager;
    private readonly IMediaStreamRepository _mediaStreamRepository;

    public ItemTextRepairer(ILogger<ItemTextRepairer> logger, IChapterManager chapterManager, IMediaStreamRepository mediaStreamRepository)
    {
        _logger = logger;
        _chapterManager = chapterManager;
        _mediaStreamRepository = mediaStreamRepository;
    }

    /// <summary>Repairs the item. Saves it only when something changed.</summary>
    public async Task<RepairOutcome> RepairAsync(BaseItem item, CancellationToken cancellationToken)
    {
        var unresolved = new List<string>();
        var changed = RepairFields(item, unresolved);

        if (changed)
        {
            // Sort name and clean name are derived from the name: let Jellyfin recompute them.
            if (string.IsNullOrEmpty(item.ForcedSortName))
            {
                item.SortName = null!;
            }

            // MetadataImport (and not MetadataEdit): Jellyfin's metadata savers, such as the NFO saver,
            // only run from ImageUpdate upwards, so nothing is written back to the drives.
            await item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken).ConfigureAwait(false);
        }

        var chapters = RepairChapters(item);
        var streams = RepairStreams(item, cancellationToken);

        if (changed || chapters > 0 || streams > 0)
        {
            _logger.LogInformation(
                "JellyUSB: textes réparés pour \"{Name}\" ({Path}) : fiche={Item}, chapitres={Chapters}, pistes={Streams}",
                item.Name,
                item.Path,
                changed,
                chapters,
                streams);
        }

        foreach (var field in unresolved)
        {
            _logger.LogWarning(
                "JellyUSB: caractère illisible (�) non réparable automatiquement dans {Field} de \"{Name}\" ({Path})",
                field,
                item.Name,
                item.Path);
        }

        return new RepairOutcome(changed, chapters, streams, unresolved);
    }

    /// <summary>
    /// Copies the text fields that contain replacement characters from <paramref name="source"/>
    /// (the same item read again from its NFO with the right encoding). Does not save.
    /// </summary>
    /// <returns>The number of fields copied.</returns>
    public static int ApplyCleanTextFrom(BaseItem item, BaseItem source)
    {
        var count = 0;

        string? Take(string? current, string? clean)
        {
            if (Mojibake.HasReplacementChar(current) && !string.IsNullOrEmpty(clean) && !Mojibake.HasReplacementChar(clean))
            {
                count++;
                return clean;
            }

            return current;
        }

        item.Name = Take(item.Name, source.Name)!;
        item.OriginalTitle = Take(item.OriginalTitle, source.OriginalTitle)!;
        item.ForcedSortName = Take(item.ForcedSortName, source.ForcedSortName)!;
        item.Overview = Take(item.Overview, source.Overview)!;
        item.Tagline = Take(item.Tagline, source.Tagline)!;
        return count;
    }

    /// <summary>Saves the item without running the metadata savers (see <see cref="RepairAsync"/>).</summary>
    public static Task SaveAsync(BaseItem item, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(item.ForcedSortName))
        {
            item.SortName = null!;
        }

        return item.UpdateToRepositoryAsync(ItemUpdateType.MetadataImport, cancellationToken);
    }

    /// <summary>Returns true when the item has text that needs repair.</summary>
    public static bool NeedsRepair(BaseItem item)
    {
        return GetTexts(item).Any(t => t is not null && (Mojibake.HasReplacementChar(t) || Mojibake.FixDoubleEncoding(t) != t));
    }

    /// <summary>Returns true when the item has replacement characters, which only a re-read of the NFO can fix.</summary>
    public static bool HasReplacementChars(BaseItem item) => GetTexts(item).Any(Mojibake.HasReplacementChar);

    internal static bool RepairFields(BaseItem item, List<string> unresolved)
    {
        var changed = false;

        string? Fix(string? value, string field, params string?[] candidates)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            var fixedValue = Mojibake.FixDoubleEncoding(value);
            if (Mojibake.HasReplacementChar(fixedValue))
            {
                var filled = Mojibake.FillReplacementChars(fixedValue, candidates.Select(c => c is null ? null : Mojibake.FixDoubleEncoding(c)));
                if (filled is not null)
                {
                    fixedValue = filled;
                }
                else
                {
                    unresolved.Add(field);
                }
            }

            if (!string.Equals(fixedValue, value, StringComparison.Ordinal))
            {
                changed = true;
            }

            return fixedValue;
        }

        item.Name = Fix(item.Name, "Name", item.OriginalTitle, item.ForcedSortName)!;
        item.OriginalTitle = Fix(item.OriginalTitle, "OriginalTitle", item.Name)!;
        item.ForcedSortName = Fix(item.ForcedSortName, "ForcedSortName", item.Name, item.OriginalTitle)!;
        item.Overview = Fix(item.Overview, "Overview")!;
        item.Tagline = Fix(item.Tagline, "Tagline")!;
        item.Genres = FixArray(item.Genres, ref changed);
        item.Studios = FixArray(item.Studios, ref changed);
        item.Tags = FixArray(item.Tags, ref changed);

        return changed;
    }

    private static string[] FixArray(string[]? values, ref bool changed)
    {
        if (values is null || values.Length == 0)
        {
            return values ?? [];
        }

        var result = values.Select(v => string.IsNullOrEmpty(v) ? v : Mojibake.FixDoubleEncoding(v)).Distinct(StringComparer.Ordinal).ToArray();
        if (!result.SequenceEqual(values, StringComparer.Ordinal))
        {
            changed = true;
        }

        return result;
    }

    private static IEnumerable<string?> GetTexts(BaseItem item)
    {
        yield return item.Name;
        yield return item.OriginalTitle;
        yield return item.ForcedSortName;
        yield return item.Overview;
        yield return item.Tagline;
        foreach (var value in (item.Genres ?? []).Concat(item.Studios ?? []).Concat(item.Tags ?? []))
        {
            yield return value;
        }
    }

    private int RepairChapters(BaseItem item)
    {
        if (item is not Video || !_chapterManager.Supports(item))
        {
            return 0;
        }

        var chapters = _chapterManager.GetChapters(item.Id);
        var count = 0;
        foreach (var chapter in chapters)
        {
            if (string.IsNullOrEmpty(chapter.Name))
            {
                continue;
            }

            var fixedName = Mojibake.FixDoubleEncoding(chapter.Name);
            if (!string.Equals(fixedName, chapter.Name, StringComparison.Ordinal))
            {
                chapter.Name = fixedName;
                count++;
            }
        }

        if (count > 0)
        {
            _chapterManager.SaveChapters(item, chapters);
        }

        return count;
    }

    private int RepairStreams(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is not Video)
        {
            return 0;
        }

        var streams = _mediaStreamRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.Id });
        var count = 0;
        foreach (var stream in streams)
        {
            if (string.IsNullOrEmpty(stream.Title))
            {
                continue;
            }

            var fixedTitle = Mojibake.FixDoubleEncoding(stream.Title);
            if (!string.Equals(fixedTitle, stream.Title, StringComparison.Ordinal))
            {
                stream.Title = fixedTitle;
                count++;
            }
        }

        if (count > 0)
        {
            _mediaStreamRepository.SaveMediaStreams(item.Id, streams, cancellationToken);
        }

        return count;
    }
}
