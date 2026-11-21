using Jellyfin.Plugin.JellyUsb.TextRepair;
using Jellyfin.Plugin.JellyUsb.Playback;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jellyfin.Plugin.JellyUsb;

/// <summary>Registers the JellyUSB services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Replaces Jellyfin's file system (registered before plugins) so that an unplugged drive behind
        // an automount point no longer makes the whole library scan fail.
        serviceCollection.Replace(ServiceDescriptor.Singleton<IFileSystem, ResilientFileSystem>());

        serviceCollection.AddSingleton<MountTable>();
        serviceCollection.AddSingleton<MediaPresenceChecker>();
        serviceCollection.AddTransient<IStartupFilter, JellyUsbStartupFilter>();

        serviceCollection.AddSingleton<Web.ActivityService>();
        serviceCollection.AddSingleton<ItemTextRepairer>();
        serviceCollection.AddHostedService<ImportRepairService>();
    }
}
