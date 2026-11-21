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
/// Jellyfin's file system, except that a folder whose device is gone is listed as empty instead of
/// throwing. An automount point (x-systemd.automount) whose USB drive is unplugged answers
/// "No such device" (ENODEV); Jellyfin 12.4 does not catch that error while checking library roots,
/// so one absent drive made the whole library scan fail and no other drive was scanned.
/// Listed as empty, the root is seen as "inaccessible or empty": its items are kept and the scan goes on.
/// Only "device gone" errors are handled; any other I/O error still surfaces.
/// </summary>
public class ResilientFileSystem : ManagedFileSystem
{
    // errno values: ENXIO 6 (no such device or address), ENODEV 19, ENOTCONN 107 (dead FUSE mount)
    private static readonly int[] DeviceGoneErrors = [6, 19, 107];
    private static readonly TimeSpan LogInterval = TimeSpan.FromMinutes(30);

    private readonly ILogger<ManagedFileSystem> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _logged = new(StringComparer.Ordinal);

    public ResilientFileSystem(
        ILogger<ManagedFileSystem> logger,
        IApplicationPaths applicationPaths,
        IEnumerable<IShortcutHandler> shortcutHandlers)
        : base(logger, applicationPaths, shortcutHandlers)
    {
        _logger = logger;
        _logger.LogInformation("JellyUSB: système de fichiers tolérant aux supports absents actif");
    }

    /// <summary>Returns true for the errors of a device that is not there (unplugged drive, dead mount).</summary>
    public static bool IsDeviceGone(Exception ex)
    {
        if (ex is not IOException || ex is FileNotFoundException || ex is DirectoryNotFoundException)
        {
            return false;
        }

        if (Array.IndexOf(DeviceGoneErrors, ex.HResult) >= 0)
        {
            return true;
        }

        var message = ex.Message;
        return message.Contains("No such device", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Transport endpoint is not connected", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enumerates <paramref name="factory"/>, stopping quietly (as if the folder were empty or ended)
    /// when the device is gone. Errors can happen when the enumeration starts or while it runs.
    /// </summary>
    public static IEnumerable<T> Guard<T>(Func<IEnumerable<T>> factory, Action<Exception> onDeviceGone)
    {
        IEnumerator<T>? enumerator = null;
        try
        {
            enumerator = factory().GetEnumerator();
        }
        catch (Exception ex) when (IsDeviceGone(ex))
        {
            onDeviceGone(ex);
        }

        if (enumerator is null)
        {
            yield break;
        }

        using (enumerator)
        {
            while (true)
            {
                T current;
                var gone = false;
                try
                {
                    if (!enumerator.MoveNext())
                    {
                        yield break;
                    }

                    current = enumerator.Current;
                }
                catch (Exception ex) when (IsDeviceGone(ex))
                {
                    onDeviceGone(ex);
                    gone = true;
                    current = default!;
                }

                if (gone)
                {
                    yield break;
                }

                yield return current;
            }
        }
    }

    /// <inheritdoc />
    public override IEnumerable<string> GetFileSystemEntryPaths(string path, bool recursive = false)
        => Guard(() => base.GetFileSystemEntryPaths(path, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFileSystemEntries(string path, bool recursive = false)
        => Guard(() => base.GetFileSystemEntries(path, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetDirectories(string path, bool recursive = false)
        => Guard(() => base.GetDirectories(path, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFiles(string path, bool recursive = false)
        => Guard(() => base.GetFiles(path, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFiles(string path, string searchPattern, bool recursive = false)
        => Guard(() => base.GetFiles(path, searchPattern, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFiles(string path, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive = false)
        => Guard(() => base.GetFiles(path, extensions, enableCaseSensitiveExtensions, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<FileSystemMetadata> GetFiles(string path, string searchPattern, IReadOnlyList<string>? extensions, bool enableCaseSensitiveExtensions, bool recursive = false)
        => Guard(() => base.GetFiles(path, searchPattern, extensions, enableCaseSensitiveExtensions, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<string> GetDirectoryPaths(string path, bool recursive = false)
        => Guard(() => base.GetDirectoryPaths(path, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<string> GetFilePaths(string path, bool recursive = false)
        => Guard(() => base.GetFilePaths(path, recursive), ex => LogGone(path, ex));

    /// <inheritdoc />
    public override IEnumerable<string> GetFilePaths(string path, string[]? extensions, bool enableCaseSensitiveExtensions, bool recursive = false)
        => Guard(() => base.GetFilePaths(path, extensions, enableCaseSensitiveExtensions, recursive), ex => LogGone(path, ex));

    private void LogGone(string path, Exception ex)
    {
        var now = DateTime.UtcNow;
        if (_logged.TryGetValue(path, out var last) && now - last < LogInterval)
        {
            return;
        }

        _logged[path] = now;
        _logger.LogWarning("JellyUSB: support absent pour {Path} ({Message}) : dossier traité comme vide, ses éléments sont conservés", path, ex.Message);
    }
}
