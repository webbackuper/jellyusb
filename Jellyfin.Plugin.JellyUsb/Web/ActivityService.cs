using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyUsb.Playback;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.JellyUsb.Web;

/// <summary>A scheduled task that is running.</summary>
public record RunningTask(string Name, string Category, double? Progress, bool IsJellyUsb);

/// <summary>Something that holds a file open on a USB medium.</summary>
public record MediaUser(string Kind, string Description, string? File, int? ProcessId, double? RunningSeconds);

/// <summary>State of one USB medium.</summary>
public record MediaState(string MediaId, string Path, bool Present, IReadOnlyList<MediaUser> Users);

/// <summary>What the server is doing right now.</summary>
public record Activity(DateTime GeneratedUtc, IReadOnlyList<RunningTask> Tasks, IReadOnlyList<MediaState> Media, string? Note);

/// <summary>
/// Collects the running tasks and, for each USB medium, what keeps it busy: files opened by the server
/// or by its ffmpeg processes, and playback sessions. Read-only; reads /proc for processes of the
/// Jellyfin user only (other users' processes are not visible without root).
/// </summary>
public class ActivityService
{
    private readonly ITaskManager _taskManager;
    private readonly ISessionManager _sessionManager;
    private readonly MountTable _mountTable;

    public ActivityService(ITaskManager taskManager, ISessionManager sessionManager, MountTable mountTable)
    {
        _taskManager = taskManager;
        _sessionManager = sessionManager;
        _mountTable = mountTable;
    }

    public Activity GetActivity()
    {
        var config = Plugin.CurrentConfiguration;
        var basePath = config.BasePath.TrimEnd('/');

        var tasks = _taskManager.ScheduledTasks
            .Where(t => t.State == TaskState.Running || t.State == TaskState.Cancelling)
            .Select(t => new RunningTask(t.Name, t.Category, t.CurrentProgress, t.ScheduledTask is TextRepair.RepairEncodingTask))
            .OrderBy(t => t.Name, StringComparer.CurrentCulture)
            .ToList();

        var users = new Dictionary<string, List<MediaUser>>(StringComparer.Ordinal);
        void Add(string? path, MediaUser user)
        {
            var mediaId = MediaPresenceChecker.GetMediaId(path, basePath);
            if (mediaId is null)
            {
                return;
            }

            if (!users.TryGetValue(mediaId, out var list))
            {
                users[mediaId] = list = new List<MediaUser>();
            }

            if (!list.Any(u => u.ProcessId == user.ProcessId && u.File == user.File && u.Kind == user.Kind))
            {
                list.Add(user);
            }
        }

        foreach (var session in _sessionManager.Sessions)
        {
            var path = session.FullNowPlayingItem?.Path;
            if (path is not null)
            {
                var what = session.TranscodingInfo is null ? "lecture directe" : "lecture avec transcodage";
                Add(path, new MediaUser("Lecture", $"{session.UserName} sur {session.DeviceName} ({session.Client}) : {what}", path, null, null));
            }
        }

        string? note = null;
        try
        {
            ScanProcesses(basePath, Add);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note = "Liste des processus indisponible : " + ex.Message;
        }

        var media = new List<MediaState>();
        var ids = new SortedSet<string>(users.Keys, StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(basePath))
            {
                foreach (var dir in Directory.EnumerateDirectories(basePath))
                {
                    ids.Add(System.IO.Path.GetFileName(dir));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // listing /mnt/usb itself never touches the drives
        }

        foreach (var id in ids)
        {
            var path = basePath + "/" + id;
            var state = _mountTable.GetState(path);
            var present = state == MountState.Mounted;
            if (state == MountState.NotAMountPoint)
            {
                try
                {
                    present = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    present = false;
                }
            }
            media.Add(new MediaState(id, path, present, users.TryGetValue(id, out var list) ? list : []));
        }

        return new Activity(DateTime.UtcNow, tasks, media, note);
    }

    private static void ScanProcesses(string basePath, Action<string?, MediaUser> add)
    {
        var self = Environment.ProcessId;
        var prefix = basePath + "/";
        var bootTime = ReadBootTime();
        var ticks = 100.0; // USER_HZ on Linux

        foreach (var dir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(System.IO.Path.GetFileName(dir), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                continue;
            }

            IEnumerable<string> fds;
            try
            {
                fds = Directory.EnumerateFileSystemEntries(dir + "/fd").ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue; // not our process
            }

            var files = new List<string>();
            foreach (var fd in fds)
            {
                try
                {
                    var target = new FileInfo(fd).LinkTarget;
                    if (target is not null && target.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        files.Add(target);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            string cmdline = string.Empty;
            try
            {
                cmdline = File.ReadAllText(dir + "/cmdline").Replace('\0', ' ').Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            var name = pid == self ? "Jellyfin" : ProcessName(dir);

            // ffmpeg reads its input through "-i <path>": count it even between two reads.
            if (files.Count == 0
                && (name.StartsWith("ffmpeg", StringComparison.Ordinal) || name.StartsWith("ffprobe", StringComparison.Ordinal))
                && cmdline.Contains(prefix, StringComparison.Ordinal))
            {
                var start = cmdline.IndexOf(prefix, StringComparison.Ordinal);
                var end = cmdline.IndexOf(" -", start, StringComparison.Ordinal);
                files.Add(end < 0 ? cmdline[start..] : cmdline[start..end]);
            }

            if (files.Count == 0)
            {
                continue;
            }

            var seconds = ReadRunningSeconds(dir, bootTime, ticks);
            var kind = name;
            var description = pid == self
                ? "le serveur Jellyfin lui-même (lecture directe, analyse ou scan)"
                : name.StartsWith("ff", StringComparison.Ordinal) ? DescribeFfmpeg(cmdline) : (cmdline.Length > 120 ? cmdline[..120] + "…" : cmdline);
            foreach (var file in files.Distinct(StringComparer.Ordinal))
            {
                add(file, new MediaUser(kind, description, file, pid, seconds));
            }
        }
    }

    /// <summary>Guesses which feature launched an ffmpeg process from its arguments.</summary>
    internal static string DescribeFfmpeg(string cmdline)
    {
        if (!cmdline.Contains("ffmpeg", StringComparison.Ordinal) && !cmdline.Contains("ffprobe", StringComparison.Ordinal))
        {
            return cmdline.Length > 120 ? cmdline[..120] + "…" : cmdline;
        }

        if (cmdline.Contains("ffprobe", StringComparison.Ordinal))
        {
            return "ffprobe : analyse du fichier (scan de la médiathèque)";
        }

        if (cmdline.Contains("chromaprint", StringComparison.Ordinal))
        {
            return "ffmpeg : empreinte audio (détection des génériques, Intro Skipper)";
        }

        if (cmdline.Contains("blackframe", StringComparison.Ordinal) || cmdline.Contains("signalstats", StringComparison.Ordinal)
            || cmdline.Contains("showinfo", StringComparison.Ordinal) || cmdline.Contains("blackdetect", StringComparison.Ordinal))
        {
            return "ffmpeg : recherche des génériques (Intro Skipper / segments)";
        }

        if (cmdline.Contains("ebur128", StringComparison.Ordinal) || cmdline.Contains("loudnorm", StringComparison.Ordinal))
        {
            return "ffmpeg : mesure du volume (normalisation audio LUFS)";
        }

        if (cmdline.Contains(".m3u8", StringComparison.Ordinal) || cmdline.Contains("-f hls", StringComparison.Ordinal))
        {
            return "ffmpeg : transcodage pour une lecture en cours";
        }

        if (cmdline.Contains("trickplay", StringComparison.OrdinalIgnoreCase) || cmdline.Contains("tile=", StringComparison.Ordinal))
        {
            return "ffmpeg : images d'aperçu (Trickplay)";
        }

        if (cmdline.Contains("-vframes 1", StringComparison.Ordinal) || cmdline.Contains("-frames:v 1", StringComparison.Ordinal)
            || cmdline.Contains("image2", StringComparison.Ordinal))
        {
            return "ffmpeg : extraction d'une image (vignette ou chapitre)";
        }

        return "ffmpeg";
    }

    private static string ProcessName(string dir)
    {
        try
        {
            return File.ReadAllText(dir + "/comm").Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "processus";
        }
    }

    private static double? ReadBootTime()
    {
        try
        {
            var line = File.ReadLines("/proc/stat").FirstOrDefault(l => l.StartsWith("btime ", StringComparison.Ordinal));
            return line is null ? null : double.Parse(line[6..], CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return null;
        }
    }

    private static double? ReadRunningSeconds(string dir, double? bootTime, double ticks)
    {
        if (bootTime is null)
        {
            return null;
        }

        try
        {
            var stat = File.ReadAllText(dir + "/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var start = double.Parse(fields[19], CultureInfo.InvariantCulture) / ticks;
            return DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (bootTime.Value + start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }
}
