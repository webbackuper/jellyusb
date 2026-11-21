using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyUsb.Configuration;

/// <summary>
/// How the web client (and apps built on it) is told that a medium is missing.
/// </summary>
public enum WebDisplayMode
{
    /// <summary>
    /// The request is refused and our message is sent over the session socket a moment later, so it
    /// opens on top of jellyfin-web's generic "Playback error" dialog (jellyfin-web 12.4 always shows
    /// that one when playback is refused).
    /// </summary>
    MessageOnTop = 0,

    /// <summary>
    /// Our text inside jellyfin-web's own "Playback error" dialog (the error code starts with '#', which
    /// jellyfin-web cannot translate and shows as is). jellyfin-web then opens its generic dialog on top.
    /// Works without the session socket.
    /// </summary>
    SingleDialog = 1
}

/// <summary>
/// Plugin configuration, edited from the dashboard page.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets a value indicating whether playback is checked before it starts.</summary>
    public bool EnablePlaybackCheck { get; set; } = true;

    /// <summary>Gets or sets the base path; the MediaID is the first folder below it.</summary>
    public string BasePath { get; set; } = "/mnt/usb/";

    /// <summary>Gets or sets the header used when the medium is not plugged in.</summary>
    public string MissingMediaHeader { get; set; } = "Support absent";

    /// <summary>Gets or sets the text used when the medium is not plugged in. Placeholders: {title}, {mediaId}, {path}.</summary>
    public string MissingMediaText { get; set; } =
        "« {title} » n'est pas présent sur le serveur. Insérez le support {mediaId} puis relancez la lecture.";

    /// <summary>Gets or sets the header used when the medium is present but the file is not.</summary>
    public string MissingFileHeader { get; set; } = "Fichier introuvable";

    /// <summary>Gets or sets the text used when the medium is present but the file is not.</summary>
    public string MissingFileText { get; set; } =
        "« {title} » est introuvable sur le support {mediaId} (le fichier a peut-être été déplacé ou renommé).";

    /// <summary>
    /// Gets or sets the short text for toasts and notifications (Android TV, Kodi), shown without header.
    /// Android TV cuts toasts after two lines: the MediaID comes first.
    /// </summary>
    public string ShortMissingMediaText { get; set; } = "Insérez le support {mediaId} : « {title} » est absent du serveur.";

    /// <summary>Gets or sets the short text for toasts and notifications when the file is missing.</summary>
    public string ShortMissingFileText { get; set; } = "Fichier introuvable sur le support {mediaId} : « {title} ».";

    /// <summary>Gets or sets how the web client displays the message.</summary>
    public WebDisplayMode WebDisplayMode { get; set; } = WebDisplayMode.MessageOnTop;

    /// <summary>Gets or sets the delay before sending the message to the web client, so it opens on top.</summary>
    public int WebMessageDelayMs { get; set; } = 800;

    /// <summary>Gets or sets the delay before sending the message to Android TV, so it shows after the error toast.</summary>
    public int AndroidTvMessageDelayMs { get; set; } = 300;

    /// <summary>Gets or sets a value indicating whether NFO files that are not UTF-8 are read as Windows-1252.</summary>
    public bool EnableNfoEncodingFallback { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether text is repaired when items are added or refreshed.</summary>
    public bool EnableImportRepair { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the repair task may ask TheMovieDb (by id) for the title
    /// when neither the NFO nor the original title give the missing letters.
    /// </summary>
    public bool UseOnlineTitlesForRepair { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the repair task runs after each library scan.</summary>
    public bool RepairAfterLibraryScan { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether the repair task already ran once manually.
    /// The automatic run after scans only starts after that first manual run.
    /// </summary>
    public bool RepairTaskRanOnce { get; set; }
}
