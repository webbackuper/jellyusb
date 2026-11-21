using Jellyfin.Plugin.JellyUsb.TextRepair;
using Jellyfin.Plugin.JellyUsb.Playback;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.JellyUsb;

/// <summary>Registers the JellyUSB services.</summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<MountTable>();
        serviceCollection.AddSingleton<MediaPresenceChecker>();
        serviceCollection.AddTransient<IStartupFilter, JellyUsbStartupFilter>();

        serviceCollection.AddSingleton<ItemTextRepairer>();
        serviceCollection.AddHostedService<ImportRepairService>();
    }
}
