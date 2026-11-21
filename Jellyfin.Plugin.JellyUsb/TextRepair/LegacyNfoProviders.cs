using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.XbmcMetadata.Parsers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.TextRepair;

/// <summary>Helpers for NFO files that are not UTF-8.</summary>
public static class LegacyNfo
{
    /// <summary>Name of the JellyUSB local metadata reader, as shown in the library settings.</summary>
    public const string ProviderName = "JellyUSB NFO";

    private static readonly Regex EncodingDeclaration = new(
        "(<\\?xml[^>]*?encoding\\s*=\\s*)([\"'])[^\"']*\\2",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Returns the NFO content decoded as Windows-1252 when the file is not valid UTF-8,
    /// or null when the standard (UTF-8) reader can read it correctly.
    /// </summary>
    public static string? TryDecodeLegacy(ReadOnlySpan<byte> bytes)
    {
        // UTF-16/UTF-32 byte order marks: the standard reader handles them.
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return null;
        }

        if (Mojibake.IsValidUtf8(bytes))
        {
            return null;
        }

        var text = Mojibake.DecodeWindows1252(bytes);
        return EncodingDeclaration.Replace(text, "$1$2utf-8$2", 1);
    }

    /// <summary>Paths where Jellyfin looks for a movie NFO (same order as MovieNfoSaver).</summary>
    public static IEnumerable<string> GetMovieNfoPaths(ItemInfo item)
    {
        var path = item.ContainingFolderPath;
        if (item.VideoType == VideoType.Dvd && !item.IsPlaceHolder)
        {
            yield return Path.Combine(path, "VIDEO_TS", "VIDEO_TS.nfo");
        }

        if (!item.IsInMixedFolder && item.ItemType == typeof(Movie))
        {
            yield return Path.Combine(path, "movie.nfo");
        }

        if (!item.IsPlaceHolder && (item.VideoType == VideoType.Dvd || item.VideoType == VideoType.BluRay))
        {
            yield return Path.Combine(path, Path.GetFileName(path) + ".nfo");
        }
        else
        {
            yield return Path.ChangeExtension(item.Path, ".nfo");
        }
    }
}

/// <summary>
/// Local metadata reader for NFO files written in Windows-1252/Latin-1. Placed before the standard
/// "Nfo" reader: for files that are valid UTF-8 it returns nothing and the standard reader runs.
/// For legacy files it converts the content to UTF-8 and runs Jellyfin's own NFO parser on it, so
/// every field is read exactly like a correct NFO.
/// </summary>
/// <typeparam name="T">Item type.</typeparam>
public abstract class LegacyNfoProvider<T> : ILocalMetadataProvider<T>, IHasItemChangeMonitor, IHasOrder
    where T : BaseItem, new()
{
    private readonly IFileSystem _fileSystem;
    private readonly IApplicationPaths _appPaths;

    protected LegacyNfoProvider(
        ILogger logger,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        IConfigurationManager config,
        IProviderManager providerManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IDirectoryService directoryService)
    {
        Logger = logger;
        _fileSystem = fileSystem;
        _appPaths = appPaths;
        Config = config;
        ProviderManager = providerManager;
        UserManager = userManager;
        UserDataManager = userDataManager;
        DirectoryService = directoryService;
    }

    /// <inheritdoc />
    public string Name => LegacyNfo.ProviderName;

    /// <inheritdoc />
    public int Order => 0;

    protected ILogger Logger { get; }

    protected IConfigurationManager Config { get; }

    protected IProviderManager ProviderManager { get; }

    protected IUserManager UserManager { get; }

    protected IUserDataManager UserDataManager { get; }

    protected IDirectoryService DirectoryService { get; }

    /// <inheritdoc />
    public Task<MetadataResult<T>> GetMetadata(ItemInfo info, IDirectoryService directoryService, CancellationToken cancellationToken)
    {
        var result = new MetadataResult<T>();
        if (!Plugin.CurrentConfiguration.EnableNfoEncodingFallback)
        {
            return Task.FromResult(result);
        }

        var file = GetNfoFile(info, directoryService);
        if (file is null)
        {
            return Task.FromResult(result);
        }

        string? text;
        try
        {
            text = LegacyNfo.TryDecodeLegacy(File.ReadAllBytes(file.FullName));
        }
        catch (IOException)
        {
            return Task.FromResult(result);
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(result);
        }

        if (text is null)
        {
            return Task.FromResult(result);
        }

        Directory.CreateDirectory(_appPaths.TempDirectory);
        var tempFile = Path.Combine(_appPaths.TempDirectory, "jellyusb-" + Guid.NewGuid().ToString("N") + ".nfo");
        try
        {
            File.WriteAllText(tempFile, text, new UTF8Encoding(false));
            result.Item = new T { IndexNumber = info.IndexNumber };
            Fetch(result, tempFile, cancellationToken);
            result.HasMetadata = true;
            Logger.LogInformation("JellyUSB: NFO en Windows-1252 lu et converti : {Path}", file.FullName);
        }
        catch (IOException ex)
        {
            Logger.LogWarning(ex, "JellyUSB: could not read the legacy NFO {Path}", file.FullName);
            result.HasMetadata = false;
        }
        finally
        {
            try
            {
                File.Delete(tempFile);
            }
            catch (IOException)
            {
            }
        }

        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public bool HasChanged(BaseItem item, IDirectoryService directoryService)
    {
        var file = GetNfoFile(new ItemInfo(item), directoryService);
        if (file?.Exists is not true)
        {
            return false;
        }

        return (_fileSystem.GetLastWriteTimeUtc(file) - item.DateLastSaved) > TimeSpan.FromMinutes(1);
    }

    protected abstract FileSystemMetadata? GetNfoFile(ItemInfo info, IDirectoryService directoryService);

    protected abstract void Fetch(MetadataResult<T> result, string path, CancellationToken cancellationToken);
}

/// <summary>Legacy NFO reader for movies.</summary>
public class LegacyMovieNfoProvider : LegacyNfoProvider<Movie>
{
    public LegacyMovieNfoProvider(
        ILogger<LegacyMovieNfoProvider> logger,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        IConfigurationManager config,
        IProviderManager providerManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IDirectoryService directoryService)
        : base(logger, fileSystem, appPaths, config, providerManager, userManager, userDataManager, directoryService)
    {
    }

    /// <inheritdoc />
    protected override FileSystemMetadata? GetNfoFile(ItemInfo info, IDirectoryService directoryService)
        => LegacyNfo.GetMovieNfoPaths(info).Select(directoryService.GetFile).FirstOrDefault(f => f is not null);

    /// <inheritdoc />
    protected override void Fetch(MetadataResult<Movie> result, string path, CancellationToken cancellationToken)
    {
        var video = new MetadataResult<Video> { Item = result.Item };
        new MovieNfoParser(Logger, Config, ProviderManager, UserManager, UserDataManager, DirectoryService).Fetch(video, path, cancellationToken);
        result.Item = (Movie)video.Item;
        result.People = video.People;
        result.Images = video.Images;
        result.RemoteImages = video.RemoteImages;
    }
}

/// <summary>Legacy NFO reader for episodes.</summary>
public class LegacyEpisodeNfoProvider : LegacyNfoProvider<Episode>
{
    public LegacyEpisodeNfoProvider(
        ILogger<LegacyEpisodeNfoProvider> logger,
        IFileSystem fileSystem,
        IApplicationPaths appPaths,
        IConfigurationManager config,
        IProviderManager providerManager,
        IUserManager userManager,
        IUserDataManager userDataManager,
        IDirectoryService directoryService)
        : base(logger, fileSystem, appPaths, config, providerManager, userManager, userDataManager, directoryService)
    {
    }

    /// <inheritdoc />
    protected override FileSystemMetadata? GetNfoFile(ItemInfo info, IDirectoryService directoryService)
        => directoryService.GetFile(Path.ChangeExtension(info.Path, ".nfo"));

    /// <inheritdoc />
    protected override void Fetch(MetadataResult<Episode> result, string path, CancellationToken cancellationToken)
        => new EpisodeNfoParser(Logger, Config, ProviderManager, UserManager, UserDataManager, DirectoryService).Fetch(result, path, cancellationToken);
}
