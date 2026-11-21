using System;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>Family of client, which decides how the message is shown.</summary>
public enum ClientKind
{
    /// <summary>jellyfin-web and the apps that embed it (Android mobile, desktop, iOS app).</summary>
    WebUi,

    /// <summary>Jellyfin for Android TV: shows messages as toasts.</summary>
    AndroidTv,

    /// <summary>jellyfin-kodi and JellyCon: shows messages as Kodi notifications.</summary>
    Kodi,

    /// <summary>Swiftfin, Findroid, Infuse and others: may ignore the message.</summary>
    Other
}

/// <summary>Maps the "Client" field of the authorization header to a <see cref="ClientKind"/>.</summary>
public static class ClientClassifier
{
    private static readonly string[] WebUiClients =
    [
        "Jellyfin Web",
        "Jellyfin for Android", // jellyfin-android: the UI is jellyfin-web
        "Jellyfin Media Player",
        "Jellyfin Desktop",
        "Jellyfin iOS",
        "Jellyfin Mobile"
    ];

    public static ClientKind Classify(string? client)
    {
        if (string.IsNullOrWhiteSpace(client))
        {
            return ClientKind.Other;
        }

        // Check Android TV first: "Jellyfin for Android TV" also starts with "Jellyfin for Android".
        if (client.Contains("Android TV", StringComparison.OrdinalIgnoreCase))
        {
            return ClientKind.AndroidTv;
        }

        if (client.Contains("Kodi", StringComparison.OrdinalIgnoreCase)
            || client.Contains("JellyCon", StringComparison.OrdinalIgnoreCase))
        {
            return ClientKind.Kodi;
        }

        foreach (var name in WebUiClients)
        {
            if (client.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                return ClientKind.WebUi;
            }
        }

        return ClientKind.Other;
    }
}
