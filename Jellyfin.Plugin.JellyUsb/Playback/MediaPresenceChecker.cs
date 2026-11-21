using System;
using System.IO;
using System.Linq;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>Result of a presence check.</summary>
public enum PresenceStatus
{
    /// <summary>The path is not under the base path: JellyUSB does not handle it.</summary>
    NotManaged,

    /// <summary>The medium is plugged in and the file exists.</summary>
    Present,

    /// <summary>The medium (MediaID) is not plugged in.</summary>
    MediaAbsent,

    /// <summary>The medium is plugged in but the file is not there.</summary>
    FileMissing
}

/// <summary>Presence check result with the MediaID found in the path.</summary>
public readonly record struct PresenceResult(PresenceStatus Status, string? MediaId);

/// <summary>
/// Decides whether the file of an item is playable, without blocking on absent automounts.
/// </summary>
public class MediaPresenceChecker
{
    private readonly MountTable _mountTable;

    public MediaPresenceChecker(MountTable mountTable)
    {
        _mountTable = mountTable;
    }

    /// <summary>
    /// Gets the MediaID of <paramref name="path"/>: the first folder below <paramref name="basePath"/>.
    /// </summary>
    public static string? GetMediaId(string? path, string basePath)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        var prefix = basePath.TrimEnd('/') + "/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = path.AsSpan(prefix.Length);
        var slash = rest.IndexOf('/');
        var mediaId = slash < 0 ? rest.ToString() : rest[..slash].ToString();
        return mediaId.Length == 0 || mediaId == "." || mediaId == ".." ? null : mediaId;
    }

    /// <summary>Checks the file at <paramref name="path"/>.</summary>
    public PresenceResult Check(string? path, string basePath)
    {
        var mediaId = GetMediaId(path, basePath);
        if (mediaId is null || path is null)
        {
            return new PresenceResult(PresenceStatus.NotManaged, null);
        }

        var mediaRoot = basePath.TrimEnd('/') + "/" + mediaId;
        var state = _mountTable.GetState(mediaRoot);
        if (state == MountState.UnmountedAutomount)
        {
            return new PresenceResult(PresenceStatus.MediaAbsent, mediaId);
        }

        try
        {
            if (!Directory.Exists(mediaRoot))
            {
                return new PresenceResult(PresenceStatus.MediaAbsent, mediaId);
            }

            // An empty folder that is not a mount point is a mount point waiting for its drive.
            if (state == MountState.NotAMountPoint && !Directory.EnumerateFileSystemEntries(mediaRoot).Any())
            {
                return new PresenceResult(PresenceStatus.MediaAbsent, mediaId);
            }

            if (File.Exists(path) || Directory.Exists(path))
            {
                return new PresenceResult(PresenceStatus.Present, mediaId);
            }

            return new PresenceResult(PresenceStatus.FileMissing, mediaId);
        }
        catch (IOException)
        {
            // ENODEV, "transport endpoint is not connected" (drive pulled while mounted)...
            return new PresenceResult(PresenceStatus.MediaAbsent, mediaId);
        }
        catch (UnauthorizedAccessException)
        {
            return new PresenceResult(PresenceStatus.MediaAbsent, mediaId);
        }
    }
}
