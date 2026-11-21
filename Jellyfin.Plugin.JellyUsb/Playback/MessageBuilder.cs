using System;
using System.Net;
using Jellyfin.Plugin.JellyUsb.Configuration;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>A message to display: header and plain text.</summary>
public readonly record struct UserMessage(string Header, string Text);

/// <summary>Builds the user-facing messages from the configured templates.</summary>
public static class MessageBuilder
{
    public static UserMessage Build(PluginConfiguration config, PresenceStatus status, string title, string mediaId, string path)
    {
        var (header, template) = status == PresenceStatus.FileMissing
            ? (config.MissingFileHeader, config.MissingFileText)
            : (config.MissingMediaHeader, config.MissingMediaText);

        var text = template
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{mediaId}", mediaId, StringComparison.OrdinalIgnoreCase)
            .Replace("{path}", path, StringComparison.OrdinalIgnoreCase);

        return new UserMessage(header, text);
    }

    /// <summary>Builds the short form used for toasts and notifications (no header).</summary>
    public static UserMessage BuildShort(PluginConfiguration config, PresenceStatus status, string title, string mediaId, string path)
    {
        var template = status == PresenceStatus.FileMissing ? config.ShortMissingFileText : config.ShortMissingMediaText;
        var text = template
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{mediaId}", mediaId, StringComparison.OrdinalIgnoreCase)
            .Replace("{path}", path, StringComparison.OrdinalIgnoreCase);
        return new UserMessage(string.Empty, text);
    }

    /// <summary>
    /// Builds the error code for the jellyfin-web "single dialog" mode.
    /// jellyfin-web translates "PlaybackError." + ErrorCode; the key is split on '#', so with a code
    /// starting with '#' the lookup targets an unknown module and the text after '#' is shown as is.
    /// The dialog renders HTML (sanitized), so the text is HTML-encoded and must not contain '#'.
    /// </summary>
    public static string BuildWebErrorCode(UserMessage message)
    {
        var html = WebUtility.HtmlEncode(message.Header) + " — " + WebUtility.HtmlEncode(message.Text);

        // HtmlEncode writes ' and accented letters as numeric entities (&#39;, &#233;), which contain '#'.
        // Only <, >, & and " need escaping, and those use named entities: turn numeric ones back into characters.
        html = System.Text.RegularExpressions.Regex.Replace(html, "&#(x?[0-9A-Fa-f]+);", m =>
        {
            var value = m.Groups[1].Value;
            var code = value.StartsWith('x') ? Convert.ToInt32(value[1..], 16) : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
            return char.ConvertFromUtf32(code);
        });

        return "#" + html.Replace("#", "♯", StringComparison.Ordinal);
    }
}
