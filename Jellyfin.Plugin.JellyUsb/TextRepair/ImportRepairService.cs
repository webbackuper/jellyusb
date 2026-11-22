using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.TextRepair;

/// <summary>
/// Background service:
/// <list type="bullet">
/// <item>puts the "JellyUSB NFO" reader before "Nfo" in every library that reads NFO files;</item>
/// <item>repairs the text of items as they are added or refreshed (import);</item>
/// <item>queues the repair task after each library scan, once it has been run manually.</item>
/// </list>
/// It never starts a scan and never watches the drives.
/// </summary>
public class ImportRepairService : IHostedService, IDisposable
{
    private readonly ILogger<ImportRepairService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly IServerConfigurationManager _serverConfig;
    private readonly ItemTextRepairer _repairer;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;

    public ImportRepairService(
        ILogger<ImportRepairService> logger,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        IServerConfigurationManager serverConfig,
        ItemTextRepairer repairer)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _serverConfig = serverConfig;
        _repairer = repairer;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (PluginServiceRegistrator.FileSystemProtection == "active")
        {
            _logger.LogInformation("JellyUSB: protection des supports absents {Status}", PluginServiceRegistrator.FileSystemProtection);
        }
        else
        {
            _logger.LogWarning("JellyUSB: protection des supports absents {Status}", PluginServiceRegistrator.FileSystemProtection);
        }

        try
        {
            EnsureReaderOrder();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyUSB: could not update the metadata reader order");
        }

        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _taskManager.TaskCompleted += OnTaskCompleted;
        CollectionFolder.LibraryOptionsUpdated += OnLibraryOptionsUpdated;
        _worker = Task.Run(() => WorkAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _taskManager.TaskCompleted -= OnTaskCompleted;
        CollectionFolder.LibraryOptionsUpdated -= OnLibraryOptionsUpdated;
        _queue.Writer.TryComplete();
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_worker is not null)
        {
            try
            {
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Inserts "JellyUSB NFO" right before "Nfo" in the local metadata reader order of each library.
    /// Libraries that do not read NFO files are left alone.
    /// </summary>
    private void EnsureReaderOrder()
    {
        if (!Plugin.CurrentConfiguration.EnableNfoEncodingFallback)
        {
            return;
        }

        var root = _serverConfig.ApplicationPaths.DefaultUserViewsPath;
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            var path = System.IO.Path.Combine(root, folder.Name);
            var options = CollectionFolder.GetLibraryOptions(path);
            var order = options.LocalMetadataReaderOrder;
            if (order is null || order.Length == 0)
            {
                continue;
            }

            var nfoIndex = Array.FindIndex(order, o => string.Equals(o, "Nfo", StringComparison.OrdinalIgnoreCase));
            var ourIndex = Array.FindIndex(order, o => string.Equals(o, LegacyNfo.ProviderName, StringComparison.Ordinal));
            if (nfoIndex < 0 || (ourIndex >= 0 && ourIndex < nfoIndex))
            {
                continue;
            }

            var list = order.Where(o => !string.Equals(o, LegacyNfo.ProviderName, StringComparison.Ordinal)).ToList();
            list.Insert(list.FindIndex(o => string.Equals(o, "Nfo", StringComparison.OrdinalIgnoreCase)), LegacyNfo.ProviderName);
            options.LocalMetadataReaderOrder = list.ToArray();
            CollectionFolder.SaveLibraryOptions(path, options);
            _logger.LogInformation("JellyUSB: lecteur \"{Reader}\" placé avant \"Nfo\" dans la bibliothèque {Library}", LegacyNfo.ProviderName, folder.Name);
        }
    }

    private void OnLibraryOptionsUpdated(object? sender, EventArgs e)
    {
        // A library was created or its settings saved: put our reader back before "Nfo".
        // Our own save raises this event again, but then the order is already right and nothing is saved.
        try
        {
            EnsureReaderOrder();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JellyUSB: could not update the metadata reader order");
        }
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        if (!Plugin.CurrentConfiguration.EnableImportRepair)
        {
            return;
        }

        var item = e.Item;
        if (item is null || item.IsVirtualItem || item is not (Video or Series or Season or BoxSet))
        {
            return;
        }

        if (_pending.TryAdd(item.Id, 0))
        {
            _queue.Writer.TryWrite(item.Id);
        }
    }

    private async Task WorkAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var id in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                _pending.TryRemove(id, out _);
                try
                {
                    if (_libraryManager.GetItemById(id) is { } item)
                    {
                        await _repairer.RepairAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "JellyUSB: import repair failed for item {Id}", id);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnTaskCompleted(object? sender, TaskCompletionEventArgs e)
    {
        var config = Plugin.CurrentConfiguration;
        if (!config.RepairAfterLibraryScan || !config.RepairTaskRanOnce)
        {
            return;
        }

        if (string.Equals(e.Result.Key, "RefreshLibrary", StringComparison.Ordinal) && e.Result.Status == TaskCompletionStatus.Completed)
        {
            _logger.LogInformation("JellyUSB: scan de la médiathèque terminé, réparation des titres planifiée");
            _taskManager.QueueIfNotRunning<RepairEncodingTask>();
        }
    }
}
