using System;
using System.Linq;
using System.Runtime.CompilerServices;
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
        // Replaces Jellyfin's file system (registered before plugins) to protect the library from absent
        // drives (see LibraryGuard). It extends an internal class of the server: on a Jellyfin version it
        // was not tested with, or if that class changed, it is not installed, so the server always starts.
        FileSystemProtection = TryProtectFileSystem(serviceCollection, applicationHost.ApplicationVersion);

        serviceCollection.AddSingleton<MountTable>();
        serviceCollection.AddSingleton<MediaPresenceChecker>();
        serviceCollection.AddTransient<IStartupFilter, JellyUsbStartupFilter>();

        serviceCollection.AddSingleton<Web.ActivityService>();
        serviceCollection.AddSingleton<Web.MediaLibraryService>();
        serviceCollection.AddSingleton<ItemTextRepairer>();
        serviceCollection.AddHostedService<ImportRepairService>();
    }

    /// <summary>The Jellyfin version (major.minor) the file system protection was built and tested with.</summary>
    public static readonly Version TestedServerVersion = new(12, 4);

    /// <summary>Gets what happened to the file system protection at startup (logged once the server runs).</summary>
    public static string FileSystemProtection { get; private set; } = "non initialisée";

    /// <summary>Installs <see cref="ResilientFileSystem"/> when it is safe; returns a status for the log.</summary>
    internal static string TryProtectFileSystem(IServiceCollection services, Version serverVersion)
    {
        if (serverVersion.Major != TestedServerVersion.Major || serverVersion.Minor != TestedServerVersion.Minor)
        {
            return $"désactivée : Jellyfin {serverVersion} n'est pas la version testée ({TestedServerVersion}.x). Mettez JellyUSB à jour.";
        }

        var problem = CheckManagedFileSystem();
        if (problem is not null)
        {
            return "désactivée : " + problem;
        }

        try
        {
            Replace(services);
            return "active";
        }
        catch (Exception ex)
        {
            return "désactivée : " + ex.GetType().Name + " " + ex.Message;
        }
    }

    /// <summary>Checks, by reflection only, that the server class still has what ResilientFileSystem uses.</summary>
    internal static string? CheckManagedFileSystem()
    {
        var type = Type.GetType("Emby.Server.Implementations.IO.ManagedFileSystem, Emby.Server.Implementations", throwOnError: false);
        if (type is null)
        {
            return "classe ManagedFileSystem introuvable";
        }

        var ctor = type.GetConstructors().FirstOrDefault(c =>
        {
            var p = c.GetParameters();
            return p.Length == 3
                && p[0].ParameterType.Name.StartsWith("ILogger", StringComparison.Ordinal)
                && p[1].ParameterType.Name == "IApplicationPaths"
                && p[2].ParameterType.Name.StartsWith("IEnumerable", StringComparison.Ordinal);
        });
        if (ctor is null)
        {
            return "le constructeur de ManagedFileSystem a changé";
        }

        string[] overridden = ["GetFileSystemEntryPaths", "GetFileSystemEntries", "GetDirectories", "GetFiles", "GetDirectoryPaths", "GetFilePaths"];
        foreach (var name in overridden)
        {
            if (!type.GetMethods().Any(m => m.Name == name && m.IsVirtual && !m.IsFinal))
            {
                return $"ManagedFileSystem.{name} n'est plus redéfinissable";
            }
        }

        return null;
    }

    // Separate and not inlined: if ResilientFileSystem cannot load, the failure stays inside the try above.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Replace(IServiceCollection services)
        => services.Replace(ServiceDescriptor.Singleton<IFileSystem, ResilientFileSystem>());
}
