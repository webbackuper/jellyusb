using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.JellyUsb.TextRepair;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb;

/// <summary>
/// Undoes, when JellyUSB is uninstalled from Jellyfin, what it changed outside its own folder (which
/// Jellyfin deletes itself):
/// <list type="bullet">
/// <item>the "JellyUSB NFO" reader in the reader order of each library;</item>
/// <item>the JellyUSB plugin repository in the server settings;</item>
/// <item>its settings file, its cache folder and the files of its scheduled task.</item>
/// </list>
/// Kept on purpose: the titles repaired in the database (the backups made by install.sh can restore
/// them), and the system mount setup (os/install-os.sh --uninstall). The web script and the playback
/// check stop with the plugin, at the restart Jellyfin asks for.
/// </summary>
public class UninstallCleaner
{
    /// <summary>The address of the JellyUSB plugin repository (as install.sh declares it).</summary>
    public const string RepositoryMarker = "webbackuper/jellyusb";

    private readonly IServerConfigurationManager _serverConfig;
    private readonly ITaskManager _taskManager;
    private readonly ILogger<UninstallCleaner> _logger;

    public UninstallCleaner(IServerConfigurationManager serverConfig, ITaskManager taskManager, ILogger<UninstallCleaner> logger)
    {
        _serverConfig = serverConfig;
        _taskManager = taskManager;
        _logger = logger;
        Current = this;
    }

    /// <summary>Gets the instance created at startup (the plugin calls it when it is uninstalled).</summary>
    public static UninstallCleaner? Current { get; private set; }

    /// <summary>Gets a value indicating whether the plugin is being uninstalled: nothing must be put back.</summary>
    public static bool IsUninstalling { get; private set; }

    /// <summary>Cleans up; every step is independent and failures are only logged.</summary>
    public IReadOnlyList<string> Run(string? pluginConfigurationFile)
    {
        IsUninstalling = true;
        var done = new List<string>();
        Step(done, "lecteur « JellyUSB NFO » retiré des bibliothèques", RemoveReaderFromLibraries);
        Step(done, "dépôt de plugins JellyUSB retiré", RemoveRepository);
        Step(done, "fichiers de la tâche planifiée supprimés", RemoveTaskFiles);
        Step(done, "dossier cache supprimé", () => DeleteDirectory(Path.Combine(_serverConfig.ApplicationPaths.CachePath, "jellyusb")));
        Step(done, "réglages du plugin supprimés", () => DeleteFile(pluginConfigurationFile));
        _logger.LogInformation("JellyUSB: désinstallation, nettoyage : {Done}", done.Count == 0 ? "rien à faire" : string.Join(" ; ", done));
        return done;
    }

    /// <summary>Removes <paramref name="reader"/> from the reader lists of <paramref name="options"/>; true when something changed.</summary>
    public static bool RemoveReader(MediaBrowser.Model.Configuration.LibraryOptions options, string reader)
    {
        var changed = false;
        if (options.LocalMetadataReaderOrder?.Contains(reader, StringComparer.Ordinal) == true)
        {
            options.LocalMetadataReaderOrder = options.LocalMetadataReaderOrder.Where(r => r != reader).ToArray();
            changed = true;
        }

        if (options.DisabledLocalMetadataReaders?.Contains(reader, StringComparer.Ordinal) == true)
        {
            options.DisabledLocalMetadataReaders = options.DisabledLocalMetadataReaders.Where(r => r != reader).ToArray();
            changed = true;
        }

        return changed;
    }

    /// <summary>The repositories without the JellyUSB one.</summary>
    public static MediaBrowser.Model.Updates.RepositoryInfo[] WithoutJellyUsb(IEnumerable<MediaBrowser.Model.Updates.RepositoryInfo> repositories)
        => repositories.Where(r => r.Url?.Contains(RepositoryMarker, StringComparison.OrdinalIgnoreCase) != true).ToArray();

    private bool RemoveReaderFromLibraries()
    {
        var root = _serverConfig.ApplicationPaths.DefaultUserViewsPath;
        if (!Directory.Exists(root))
        {
            return false;
        }

        var changed = false;
        foreach (var library in Directory.EnumerateDirectories(root))
        {
            if (!File.Exists(Path.Combine(library, "options.xml")))
            {
                continue;
            }

            var options = CollectionFolder.GetLibraryOptions(library);
            if (RemoveReader(options, LegacyNfo.ProviderName))
            {
                CollectionFolder.SaveLibraryOptions(library, options);
                changed = true;
            }
        }

        return changed;
    }

    private bool RemoveRepository()
    {
        var config = _serverConfig.Configuration;
        var kept = WithoutJellyUsb(config.PluginRepositories);
        if (kept.Length == config.PluginRepositories.Length)
        {
            return false;
        }

        config.PluginRepositories = kept;
        _serverConfig.SaveConfiguration();
        return true;
    }

    private bool RemoveTaskFiles()
    {
        var worker = _taskManager.ScheduledTasks.FirstOrDefault(t => t.ScheduledTask is RepairEncodingTask);
        if (worker is null)
        {
            return false;
        }

        var name = new Guid(worker.Id) + ".js";
        var paths = _serverConfig.ApplicationPaths;
        var removed = DeleteFile(Path.Combine(paths.ConfigurationDirectoryPath, "ScheduledTasks", name));
        return DeleteFile(Path.Combine(paths.DataPath, "ScheduledTasks", name)) || removed;
    }

    private static bool DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        Directory.Delete(path, true);
        return true;
    }

    private static bool DeleteFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    private void Step(List<string> done, string label, Func<bool> action)
    {
        try
        {
            if (action())
            {
                done.Add(label);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JellyUSB: désinstallation, étape non faite : {Step}", label);
        }
    }
}
