using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>Where a path is, relative to the USB media.</summary>
public enum GuardScope
{
    /// <summary>Not under the base path: Jellyfin's behaviour is kept as is.</summary>
    Outside,

    /// <summary>The base path itself (/mnt/usb).</summary>
    BasePath,

    /// <summary>The root of a medium (/mnt/usb/MOVIES02).</summary>
    MediaRoot,

    /// <summary>Somewhere inside a medium (/mnt/usb/MOVIES02/MOVIES02/...).</summary>
    InsideMedia
}

/// <summary>Whether the drive of a medium is there.</summary>
public enum MediaRootState
{
    /// <summary>Mounted, or a plain folder with something in it.</summary>
    Present,

    /// <summary>Missing, or an empty folder (a mount point waiting for its drive).</summary>
    Absent,

    /// <summary>An automount point not mounted yet: only touching it tells (and mounts it if the drive is there).</summary>
    Automount
}

/// <summary>
/// Keeps Jellyfin from removing the movies of a USB medium that is not there.
/// <para>
/// During a scan Jellyfin removes the items of a folder whose listing comes back without them, and a
/// folder it cannot find is listed as empty. A drive unplugged before or during a scan would therefore
/// lose its movies: the folders under its mount point vanish, or the listing stops halfway. Jellyfin
/// keeps the items only when the listing throws an I/O error, or when a library root is empty.
/// </para>
/// <para>
/// So, for a medium:
/// <list type="bullet">
/// <item>its root, when the drive is gone, is listed as empty when it is a library folder (Jellyfin then
/// skips it, quietly); it throws instead when the drive went away a moment ago (the scan may be reading
/// it) or when the root is not a library folder (a library on the base path itself, whose media are
/// sub-folders);</item>
/// <item>anything inside it throws an <see cref="IOException"/> when the drive is gone or goes away,
/// instead of an empty or truncated listing;</item>
/// <item>a listing is always read to the end before being returned, so it is never truncated.</item>
/// </list>
/// A folder really deleted from a drive that is there is still reported as missing.
/// </para>
/// </summary>
public class LibraryGuard
{
    // errno values: ENXIO 6 (no such device or address), ENODEV 19, ENOTCONN 107 (dead FUSE mount)
    private static readonly int[] DeviceGoneErrors = [6, 19, 107];

    /// <summary>How long a drive that went away is treated as "just unplugged".</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromMinutes(15);

    private readonly MountTable _mountTable;
    private readonly Func<string> _basePath;
    private readonly Func<string, bool?> _hasEntries;
    private readonly Func<string, bool> _isLibraryRoot;
    private readonly Func<DateTime> _now;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _lastPresent = new(StringComparer.Ordinal);

    public LibraryGuard(MountTable mountTable, Func<string> basePath, Func<string, bool>? isLibraryRoot = null)
        : this(mountTable, basePath, HasEntries, isLibraryRoot)
    {
    }

    /// <summary>Test constructor: <paramref name="hasEntries"/> answers null for a missing folder.</summary>
    public LibraryGuard(MountTable mountTable, Func<string> basePath, Func<string, bool?> hasEntries, Func<string, bool>? isLibraryRoot = null, Func<DateTime>? now = null)
    {
        _mountTable = mountTable;
        _basePath = basePath;
        _hasEntries = hasEntries;
        _isLibraryRoot = isLibraryRoot ?? (_ => true);
        _now = now ?? (() => DateTime.UtcNow);
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

    /// <summary>Where <paramref name="path"/> is, with its MediaID and the root of the medium.</summary>
    public (GuardScope Scope, string? MediaId, string? Root) Classify(string path)
    {
        var basePath = MountTable.Normalize(_basePath() ?? string.Empty);
        if (string.IsNullOrEmpty(path) || basePath.Length <= 1)
        {
            return (GuardScope.Outside, null, null);
        }

        var target = MountTable.Normalize(path);
        if (string.Equals(target, basePath, StringComparison.Ordinal))
        {
            return (GuardScope.BasePath, null, null);
        }

        var mediaId = MediaPresenceChecker.GetMediaId(target, basePath);
        if (mediaId is null)
        {
            return (GuardScope.Outside, null, null);
        }

        var root = basePath + "/" + mediaId;
        return (string.Equals(target, root, StringComparison.Ordinal) ? GuardScope.MediaRoot : GuardScope.InsideMedia, mediaId, root);
    }

    /// <summary>Whether the drive of the medium rooted at <paramref name="root"/> is there.</summary>
    public MediaRootState GetRootState(string root)
    {
        var state = ReadRootState(root);
        if (state == MediaRootState.Present)
        {
            _lastPresent[root] = _now();
        }

        return state;
    }

    /// <summary>Whether the drive of <paramref name="root"/> was seen a moment ago.</summary>
    public bool WasRecentlyPresent(string root)
        => _lastPresent.TryGetValue(root, out var last) && _now() - last < RecentWindow;

    private MediaRootState ReadRootState(string root)
    {
        switch (_mountTable.GetState(root))
        {
            case MountState.Mounted:
                return MediaRootState.Present;
            case MountState.UnmountedAutomount:
                return MediaRootState.Automount;
        }

        try
        {
            return _hasEntries(root) == true ? MediaRootState.Present : MediaRootState.Absent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return MediaRootState.Absent;
        }
    }

    /// <summary>
    /// Lists <paramref name="path"/> through <paramref name="list"/>, applying the rules above.
    /// <paramref name="probe"/> is true for the call Jellyfin uses to test whether a library folder is
    /// accessible (an empty answer there means "skip this library folder").
    /// </summary>
    public IEnumerable<T> List<T>(string path, bool probe, Func<IEnumerable<T>> list, Action<string, string>? onSkipped = null)
    {
        var (scope, mediaId, root) = Classify(path);
        if (scope is GuardScope.Outside or GuardScope.BasePath || root is null)
        {
            return list();
        }

        // read the state first: a drive mounted right now counts as "just there" if it fails while listing
        var state = GetRootState(root);
        var recentlyPresent = WasRecentlyPresent(root);

        // An empty answer is safe for the root of a library folder whose drive has been away for a while:
        // Jellyfin skips such a folder. Anywhere else an empty answer would read as "files removed".
        var quiet = scope == GuardScope.MediaRoot && (probe || (_isLibraryRoot(root) && !recentlyPresent));
        if (state == MediaRootState.Absent)
        {
            onSkipped?.Invoke(path, "support absent");
            return quiet ? [] : throw Absent(mediaId!, path, null);
        }

        try
        {
            return list().ToList();
        }
        catch (Exception ex) when (IsDeviceGone(ex))
        {
            onSkipped?.Invoke(path, ex.Message);
            return quiet ? [] : throw Absent(mediaId!, path, ex);
        }
        catch (DirectoryNotFoundException ex) when (scope == GuardScope.InsideMedia && GetRootState(root) != MediaRootState.Present)
        {
            // The folder vanished because the drive did: not a deletion.
            onSkipped?.Invoke(path, "support retiré pendant la lecture");
            throw Absent(mediaId!, path, ex);
        }
    }

    private static IOException Absent(string mediaId, string path, Exception? inner)
        => new($"JellyUSB : le support {mediaId} est absent, « {path} » n'est pas lu et rien n'est retiré de la bibliothèque", inner);

    private static bool? HasEntries(string path)
        => Directory.Exists(path) ? Directory.EnumerateFileSystemEntries(path).Any() : null;
}
