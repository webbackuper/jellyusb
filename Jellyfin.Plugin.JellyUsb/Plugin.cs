using System;
using System.Collections.Generic;
using Jellyfin.Plugin.JellyUsb.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.JellyUsb;

/// <summary>
/// JellyUSB: checks that the USB medium holding a file is present before playback,
/// and repairs badly encoded text (Windows-1252 NFO files, double-encoded UTF-8).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin identifier (also in build.yaml).</summary>
    public const string PluginGuid = "9f8d8889-7b20-47d5-ad9a-37807e42e4ce";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <inheritdoc />
    public override string Name => "JellyUSB";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse(PluginGuid);

    /// <inheritdoc />
    public override string Description =>
        "Vérifie la présence du support USB (MediaID) avant la lecture et répare les caractères accentués.";

    /// <summary>Gets the current plugin instance.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets the current configuration, or defaults when the plugin is not loaded (tests).</summary>
    public static PluginConfiguration CurrentConfiguration => Instance?.Configuration ?? new PluginConfiguration();

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return
        [
            new PluginPageInfo
            {
                Name = Name,
                DisplayName = Name,
                EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
                // Listed in the dashboard menu, under Plugins, like the other configurable plugins.
                EnableInMainMenu = true,
                MenuSection = "Plugins",
                MenuIcon = "usb"
            }
        ];
    }
}
