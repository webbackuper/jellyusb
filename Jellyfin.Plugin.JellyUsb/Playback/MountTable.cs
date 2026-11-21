using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>
/// State of the mount that covers a directory.
/// </summary>
public enum MountState
{
    /// <summary>The directory is itself a mount point of a real filesystem.</summary>
    Mounted,

    /// <summary>
    /// The directory is covered by an autofs (automount) point with nothing mounted on it.
    /// Touching it would trigger a mount attempt (several seconds, then ENODEV), so we must not.
    /// </summary>
    UnmountedAutomount,

    /// <summary>The directory is a plain folder of a parent filesystem.</summary>
    NotAMountPoint
}

/// <summary>
/// Reads /proc/self/mountinfo. Never touches the paths themselves.
/// </summary>
public class MountTable
{
    private readonly Func<string> _readMountInfo;

    public MountTable()
        : this(() => File.ReadAllText("/proc/self/mountinfo"))
    {
    }

    /// <summary>Test constructor.</summary>
    public MountTable(Func<string> readMountInfo)
    {
        _readMountInfo = readMountInfo;
    }

    /// <summary>
    /// Gets the mount state of <paramref name="directory"/>.
    /// </summary>
    public MountState GetState(string directory)
    {
        var target = Normalize(directory);

        // Mount point -> filesystem types, in mount order (last one is on top).
        var stacks = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        string text;
        try
        {
            text = _readMountInfo();
        }
        catch (IOException)
        {
            return MountState.NotAMountPoint;
        }
        catch (UnauthorizedAccessException)
        {
            return MountState.NotAMountPoint;
        }

        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseLine(line, out var mountPoint, out var fsType))
            {
                continue;
            }

            if (!stacks.TryGetValue(mountPoint, out var list))
            {
                list = new List<string>();
                stacks[mountPoint] = list;
            }

            list.Add(fsType);
        }

        // Deepest mount point that contains the target.
        string? best = null;
        foreach (var mountPoint in stacks.Keys)
        {
            if (IsSameOrParent(mountPoint, target) && (best is null || mountPoint.Length > best.Length))
            {
                best = mountPoint;
            }
        }

        if (best is null)
        {
            return MountState.NotAMountPoint;
        }

        var top = stacks[best][^1];
        if (string.Equals(top, "autofs", StringComparison.Ordinal))
        {
            return MountState.UnmountedAutomount;
        }

        return string.Equals(best, target, StringComparison.Ordinal) ? MountState.Mounted : MountState.NotAMountPoint;
    }

    /// <summary>
    /// Parses one mountinfo line: "id parent maj:min root mountpoint opts [optional...] - fstype source superopts".
    /// </summary>
    internal static bool TryParseLine(string line, out string mountPoint, out string fsType)
    {
        mountPoint = string.Empty;
        fsType = string.Empty;
        var fields = line.Split(' ');
        if (fields.Length < 7)
        {
            return false;
        }

        var separator = Array.IndexOf(fields, "-", 6);
        if (separator < 0 || separator + 1 >= fields.Length)
        {
            return false;
        }

        mountPoint = Normalize(Unescape(fields[4]));
        fsType = fields[separator + 1];
        return true;
    }

    /// <summary>Decodes the octal escapes used by the kernel (\040 for space, \011 tab, \012 newline, \134 backslash).</summary>
    internal static string Unescape(string value)
    {
        if (!value.Contains('\\', StringComparison.Ordinal))
        {
            return value;
        }

        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 3 < value.Length
                && IsOctal(value[i + 1]) && IsOctal(value[i + 2]) && IsOctal(value[i + 3]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 3), 8));
                i += 3;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(value[i].ToString()));
            }
        }

        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsOctal(char c) => c >= '0' && c <= '7';

    internal static string Normalize(string path)
    {
        if (path.Length > 1)
        {
            path = path.TrimEnd('/');
        }

        return path.Length == 0 ? "/" : path;
    }

    private static bool IsSameOrParent(string parent, string child)
    {
        if (string.Equals(parent, child, StringComparison.Ordinal))
        {
            return true;
        }

        if (parent == "/")
        {
            return child.StartsWith('/');
        }

        return child.StartsWith(parent + "/", StringComparison.Ordinal);
    }
}
