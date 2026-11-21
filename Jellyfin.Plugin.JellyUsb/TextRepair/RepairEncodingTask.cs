using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.JellyUsb.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.TextRepair;

/// <summary>
/// Scheduled task "Réparer les titres mal encodés": repairs the library text in the database.
/// Items with replacement characters are re-read from their NFO (with the Windows-1252 fallback)
/// when their medium is plugged in; double encoding and titles that match their original title are
/// repaired from the database alone. Files on the drives are never written.
/// </summary>
public class RepairEncodingTask : IScheduledTask
{
    private readonly ILogger<RepairEncodingTask> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ItemTextRepairer _repairer;
    private readonly MediaPresenceChecker _checker;
    private readonly IServiceProvider _services;
    private readonly IProviderManager _providerManager;

    public RepairEncodingTask(
        ILogger<RepairEncodingTask> logger,
        ILibraryManager libraryManager,
        ItemTextRepairer repairer,
        MediaPresenceChecker checker,
        IServiceProvider services,
        IProviderManager providerManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _repairer = repairer;
        _checker = checker;
        _services = services;
        _providerManager = providerManager;
    }

    /// <inheritdoc />
    public string Name => "Réparer les titres mal encodés";

    /// <inheritdoc />
    public string Key => "JellyUsbRepairEncoding";

    /// <inheritdoc />
    public string Description =>
        "Répare les caractères accentués mal affichés (« � », « Ã© ») dans les titres, résumés, chapitres et pistes. "
        + "Les films dont le titre contient « � » sont relus depuis leur NFO : branchez leur support USB avant de lancer la tâche.";

    /// <inheritdoc />
    public string Category => "JellyUSB";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.CurrentConfiguration;
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.Video, BaseItemKind.MusicVideo, BaseItemKind.Series, BaseItemKind.Season, BaseItemKind.BoxSet],
            IsVirtualItem = false,
            Recursive = true
        });

        var movieNfo = ActivatorUtilities.CreateInstance<LegacyMovieNfoProvider>(_services);
        var episodeNfo = ActivatorUtilities.CreateInstance<LegacyEpisodeNfoProvider>(_services);
        var directoryService = _services.GetRequiredService<IDirectoryService>();

        int repaired = 0, fromNfo = 0, fromOnline = 0, waitingForMedia = 0, unresolved = 0;
        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            try
            {
                var copied = 0;
                if (ItemTextRepairer.HasReplacementChars(item))
                {
                    var presence = _checker.Check(item.Path, config.BasePath);
                    if (presence.Status is PresenceStatus.MediaAbsent or PresenceStatus.FileMissing)
                    {
                        waitingForMedia++;
                    }
                    else
                    {
                        copied = await CopyFromNfoAsync(item, movieNfo, episodeNfo, directoryService, cancellationToken).ConfigureAwait(false);
                        if (copied > 0)
                        {
                            fromNfo++;
                            await ItemTextRepairer.SaveAsync(item, cancellationToken).ConfigureAwait(false);
                        }
                    }
                }

                // Title still broken and the original title does not give the letters: ask the online
                // provider (TheMovieDb, by id) for the title in the library language, and only fill the
                // replacement characters when it matches everywhere else.
                if (config.UseOnlineTitlesForRepair
                    && item is Movie movie
                    && Mojibake.HasReplacementChar(movie.Name)
                    && Mojibake.FillReplacementChars(movie.Name, [movie.OriginalTitle, movie.ForcedSortName]) is null)
                {
                    var online = await GetOnlineTitlesAsync(movie, cancellationToken).ConfigureAwait(false);
                    var filled = Mojibake.FillReplacementChars(movie.Name, online);
                    if (filled is not null)
                    {
                        _logger.LogInformation("JellyUSB: titre réparé depuis TheMovieDb : \"{Old}\" -> \"{New}\"", movie.Name, filled);
                        movie.Name = filled;
                        await ItemTextRepairer.SaveAsync(movie, cancellationToken).ConfigureAwait(false);
                        fromOnline++;
                        copied++;
                    }
                }

                var outcome = await _repairer.RepairAsync(item, cancellationToken).ConfigureAwait(false);
                if (outcome.AnyChange || copied > 0)
                {
                    repaired++;
                }

                if (outcome.Unresolved.Count > 0)
                {
                    unresolved++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "JellyUSB: repair failed for {Name} ({Path})", item.Name, item.Path);
            }

            progress.Report(100.0 * (i + 1) / Math.Max(1, items.Count));
        }

        _logger.LogInformation(
            "JellyUSB: réparation terminée. {Repaired} élément(s) réparé(s) dont {FromNfo} relu(s) depuis le NFO "
            + "et {FromOnline} depuis TheMovieDb ; {Unresolved} encore illisible(s) dont {Waiting} dont le support n'est pas branché.",
            repaired,
            fromNfo,
            fromOnline,
            unresolved,
            waitingForMedia);

        if (Plugin.Instance is { } plugin && !plugin.Configuration.RepairTaskRanOnce)
        {
            plugin.Configuration.RepairTaskRanOnce = true;
            plugin.SaveConfiguration();
        }
    }

    private async Task<IReadOnlyList<string?>> GetOnlineTitlesAsync(Movie movie, CancellationToken cancellationToken)
    {
        if (!movie.ProviderIds.ContainsKey("Tmdb") && !movie.ProviderIds.ContainsKey("Imdb"))
        {
            return [];
        }

        try
        {
            var lookup = movie.GetLookupInfo();
            var results = await _providerManager.GetRemoteSearchResults<Movie, MovieInfo>(
                new RemoteSearchQuery<MovieInfo>
                {
                    SearchInfo = lookup,
                    ItemId = movie.Id,
                    SearchProviderName = "TheMovieDb",
                    IncludeDisabledProviders = true
                },
                cancellationToken).ConfigureAwait(false);
            return results.Select(r => r.Name).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "JellyUSB: TheMovieDb lookup failed for {Name}", movie.Name);
            return [];
        }
    }

    private static async Task<int> CopyFromNfoAsync(
        BaseItem item,
        LegacyMovieNfoProvider movieNfo,
        LegacyEpisodeNfoProvider episodeNfo,
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        BaseItem? source = item switch
        {
            Movie => (await movieNfo.GetMetadata(new ItemInfo(item), directoryService, cancellationToken).ConfigureAwait(false)) is { HasMetadata: true } r ? r.Item : null,
            Episode => (await episodeNfo.GetMetadata(new ItemInfo(item), directoryService, cancellationToken).ConfigureAwait(false)) is { HasMetadata: true } r ? r.Item : null,
            _ => null
        };

        return source is null ? 0 : ItemTextRepairer.ApplyCleanTextFrom(item, source);
    }
}
