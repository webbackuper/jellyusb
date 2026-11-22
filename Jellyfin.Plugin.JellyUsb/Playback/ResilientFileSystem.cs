using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using Emby.Server.Implementations.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>
/// Jellyfin's file system, with the USB media guarded by <see cref="LibraryGuard"/>:
/// <list type="bullet">
/// <item>the root of an absent medium is "inaccessible or empty" (an automount point whose drive is
/// unplugged answers "No such device", which Jellyfin 12.4 does not catch: one absent drive used to make
/// the whole library scan fail);</item>
/// <item>inside an absent or vanishing medium, listings throw an I/O error, which Jellyfin takes as
/// "cannot read, keep the items", instead of an empty or truncated listing, which it would take as
/// "files removed".</item>
/// </list>
/// Paths outside the base path are left to Jellyfin.
/// </summary>
public class ResilientFileSystem : ManagedFileSystem
{
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger<ManagedFileSystem> _logger;
    private readonly LibraryGuard _guard;
    private readonly LibraryRoots _libraryRoots;
    private readonly ConcurrentDictionary<string, DateTime> _logged = new(StringComparer.Ordinal);

    public ResilientFileSystem(
        ILogger<ManagedFileSystem> logger,
        IApplicationPaths applicationPaths,
        IEnumerable<IShortcutHandler> shortcutHandlers,
        MountTable mountTable)
        : base(logger, applicationPaths, shortcutHandlers)
    {
        _logger = logger;
        var userViews = (applicationPaths as MediaBrowser.Controller.IServerApplicationPaths)?.DefaultUserViewsPath
            ?? Path.Combine(applicationPaths.ProgramDataPath, "root", "default");
        _libraryRoots = new LibraryRoots(userViews);
        _guard = new LibraryGuard(mountTable, () => Plugin.CurrentConfiguration.BasePath, _libraryRoots.Contains);
        _logger.LogInformation("JellyUSB: système de fichiers protégé (supports absents ignorés, jamais retirés)");
    }

    /// <summary>Returns true for the errors of a device that is not there (unplugged drive, dead mount).</summary>
    public static bool IsDeviceGone(Exception ex) => LibraryGuard.IsDeviceGone(ex);

    /// <inheritdoc />
    /// <remarks>The call behind Jellyfin's "is this library folder accessible?" test.</remarks>
    public override IEnumerable<string> GetFileSystemEntryPaths(string path, bool recursive = false)
        => _guard.List(path, probe: !recursive, () => base.GetFileSystemEntryPaths(path, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFileSystemEntries(string path, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetFileSystemEntries(path, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetDirectories(string path, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetDirectories(path, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFiles(string path, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetFiles(path, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFiles(string path, string searchPattern, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetFiles(path, searchPattern, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFiles(string path, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetFiles(path, extensions, enableCaseSensitiveExtensions, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFiles(string path, string searchPattern, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetFiles(path, searchPattern, extensions, enableCaseSensitiveExtensions, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<string> GetDirectoryPaths(string path, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetDirectoryPaths(path, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<string> GetFilePaths(string path, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetFilePaths(path, recursive), LogSkipped);

    /// <inheritdoc />
    public override IEnumerable<string> GetFilePaths(string path, string[]? extensions, bool enableCaseSensitiveExtensions, bool recursive = false)
        => _guard.List(path, probe: false, () => base.GetFilePaths(path, extensions, enableCaseSensitiveExtensions, recursive), LogSkipped);

    private void LogSkipped(string path, string reason)
    {
        var now = DateTime.UtcNow;
        if (_logged.TryGetValue(path, out var last) && now - last < LogInterval)
        {
            return;
        }

        _logged[path] = now;
        _logger.LogWarning("JellyUSB: {Path} non lu ({Reason}) : ses éléments restent dans la bibliothèque", path, reason);
    }
}

/// <summary>
/// The folders of the libraries, read from Jellyfin's *.mblink files (root/default/&lt;library&gt;/), with
/// plain file reads so the guarded file system does not call itself. Refreshed every minute.
/// </summary>
public sealed class LibraryRoots
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(1);
    private readonly string _userViewsPath;
    private readonly object _lock = new();
    private HashSet<string> _roots = new(StringComparer.Ordinal);
    private DateTime _readAt = DateTime.MinValue;

    public LibraryRoots(string userViewsPath)
    {
        _userViewsPath = userViewsPath;
    }

    /// <summary>Whether <paramref name="path"/> is the folder of a library.</summary>
    public bool Contains(string path)
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _readAt > RefreshInterval)
            {
                _roots = Read(_userViewsPath);
                _readAt = DateTime.UtcNow;
            }

            return _roots.Contains(MountTable.Normalize(path));
        }
    }

    internal static HashSet<string> Read(string userViewsPath)
    {
        var roots = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(userViewsPath))
            {
                return roots;
            }

            foreach (var link in Directory.EnumerateFiles(userViewsPath, "*.mblink", SearchOption.AllDirectories))
            {
                var target = File.ReadAllText(link).Trim();
                if (target.Length > 0)
                {
                    roots.Add(MountTable.Normalize(target));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unknown: every root counts as "not a library folder", which only makes the guard stricter
        }

        return roots;
    }
}

