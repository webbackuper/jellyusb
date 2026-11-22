using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyUsb.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>
/// Intercepts playback requests (PlaybackInfo, streams, downloads) for items whose USB medium
/// or file is missing, blocks them and tells the client why.
/// Registered through an <see cref="Microsoft.AspNetCore.Hosting.IStartupFilter"/>, so it runs before
/// Jellyfin's own pipeline (before BaseUrl mapping and authentication): routes are matched on the
/// end of the path and authentication is checked here.
/// </summary>
public partial class PlaybackGuardMiddleware
{
    private const string GuidPattern = "[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}";
    private static readonly TimeSpan MessageThrottle = TimeSpan.FromSeconds(10);

    private readonly RequestDelegate _next;
    private readonly ILogger<PlaybackGuardMiddleware> _logger;
    private readonly MediaPresenceChecker _checker;
    private readonly ConcurrentDictionary<string, DateTime> _lastMessages = new(StringComparer.Ordinal);

    public PlaybackGuardMiddleware(RequestDelegate next, ILogger<PlaybackGuardMiddleware> logger, MediaPresenceChecker checker)
    {
        _next = next;
        _logger = logger;
        _checker = checker;
    }

    private enum RouteKind
    {
        None,
        PlaybackInfo,
        Stream
    }

    [GeneratedRegex("/Items/(?<id>" + GuidPattern + ")/PlaybackInfo/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaybackInfoRoute();

    [GeneratedRegex(
        "/(?:Videos|Audio)/(?<id>" + GuidPattern + ")/(?:stream(?:\\.[A-Za-z0-9]+)?|master\\.m3u8|main\\.m3u8|live\\.m3u8|universal|hls1?/.+)$"
        + "|/Items/(?<id>" + GuidPattern + ")/(?:Download|File)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StreamRoute();

    /// <summary>Processes a request.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        ISessionManager sessionManager)
    {
        var config = Plugin.CurrentConfiguration;
        var path = context.Request.Path.Value;
        if (!config.EnablePlaybackCheck || string.IsNullOrEmpty(path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var (kind, itemId) = MatchRoute(path, context.Request.Method);
        if (kind == RouteKind.None)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            if (await TryBlockAsync(context, kind, itemId, config, libraryManager, authorizationContext, sessionManager).ConfigureAwait(false))
            {
                return;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never break playback because of JellyUSB.
            _logger.LogError(ex, "JellyUSB: presence check failed for {Path}", path);
        }

        await _next(context).ConfigureAwait(false);
    }

    private static (RouteKind Kind, Guid ItemId) MatchRoute(string path, string method)
    {
        var isGet = HttpMethods.IsGet(method) || HttpMethods.IsHead(method);
        var match = PlaybackInfoRoute().Match(path);
        if (match.Success && (isGet || HttpMethods.IsPost(method)))
        {
            return Guid.TryParse(match.Groups["id"].Value, out var id) ? (RouteKind.PlaybackInfo, id) : (RouteKind.None, Guid.Empty);
        }

        match = StreamRoute().Match(path);
        if (match.Success && isGet)
        {
            return Guid.TryParse(match.Groups["id"].Value, out var id) ? (RouteKind.Stream, id) : (RouteKind.None, Guid.Empty);
        }

        return (RouteKind.None, Guid.Empty);
    }

    private async Task<bool> TryBlockAsync(
        HttpContext context,
        RouteKind kind,
        Guid itemId,
        PluginConfiguration config,
        ILibraryManager libraryManager,
        IAuthorizationContext authorizationContext,
        ISessionManager sessionManager)
    {
        var item = libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return false;
        }

        // The requested version (alternate versions are separate items).
        var source = item;
        var mediaSourceId = await GetMediaSourceIdAsync(context, kind).ConfigureAwait(false);
        if (Guid.TryParse(mediaSourceId, out var sourceId) && sourceId != item.Id && libraryManager.GetItemById(sourceId) is { } version)
        {
            source = version;
        }

        if (MediaPresenceChecker.GetMediaId(source.Path, config.BasePath) is null)
        {
            return false;
        }

        var auth = await authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
        if (!auth.IsAuthenticated)
        {
            // Let Jellyfin answer 401; do not leak titles.
            return false;
        }

        if (auth.User is not null && !item.IsVisible(auth.User))
        {
            return false;
        }

        var presence = _checker.Check(source.Path, config.BasePath);
        if (presence.Status is PresenceStatus.NotManaged or PresenceStatus.Present || presence.MediaId is null)
        {
            return false;
        }

        var client = ClientClassifier.Classify(auth.Client);
        var full = MessageBuilder.Build(config, presence.Status, GetTitle(item), presence.MediaId, source.Path ?? string.Empty);
        var message = client switch
        {
            // Android TV toast: "header: text", cut after two lines.
            ClientKind.AndroidTv => MessageBuilder.BuildShort(config, presence.Status, GetTitle(item), presence.MediaId, source.Path ?? string.Empty),
            // Kodi notification: bold header plus one line.
            ClientKind.Kodi => MessageBuilder.BuildShort(config, presence.Status, GetTitle(item), presence.MediaId, source.Path ?? string.Empty) with { Header = full.Header },
            _ => full
        };
        _logger.LogInformation(
            "JellyUSB: lecture bloquée ({Status}) de \"{Title}\" ({Path}) pour {Client} / {Device}",
            presence.Status,
            item.Name,
            source.Path,
            auth.Client,
            auth.Device);

        var throttleKey = auth.DeviceId + "|" + item.Id.ToString("N");
        if (kind == RouteKind.PlaybackInfo)
        {
            if (client == ClientKind.WebUi && config.WebDisplayMode == WebDisplayMode.SingleDialog)
            {
                MarkMessageSent(throttleKey);
                await WritePlaybackInfoErrorAsync(context, MessageBuilder.BuildWebErrorCode(message)).ConfigureAwait(false);
                return true;
            }

            if (client == ClientKind.WebUi && IsCompanionPlayer(sessionManager, auth))
            {
                // Native player of an app whose UI is jellyfin-web (jellyfin-android "integrated player"):
                // it has its own session, without websocket. It shows its own short error; our message goes
                // to the app's web UI session and is there when the user leaves the player.
                SendMessage(sessionManager, auth, client, message, throttleKey, config, context.Connection.RemoteIpAddress?.ToString());
                await WritePlaybackInfoErrorAsync(context, "NotAllowed").ConfigureAwait(false);
                return true;
            }

            if (client == ClientKind.WebUi)
            {
                // jellyfin-web shows one generic dialog for a failed request. The JellyUSB script reads the
                // message from this header and writes it into that dialog; the message sent through the
                // websocket is the fallback when the script is not there.
                context.Response.Headers["X-JellyUsb-Message"] = EncodeMessageHeader(message);
                if (!context.Request.Headers.ContainsKey("X-JellyUsb-Script"))
                {
                    SendMessage(sessionManager, auth, client, message, throttleKey, config, context.Connection.RemoteIpAddress?.ToString());
                }

                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(message.Header + " : " + message.Text, context.RequestAborted).ConfigureAwait(false);
                return true;
            }

            SendMessage(sessionManager, auth, client, message, throttleKey, config, context.Connection.RemoteIpAddress?.ToString());
            if (client == ClientKind.AndroidTv && config.AndroidTvClosePlayer)
            {
                ClosePlayerLater(sessionManager, auth, client, config, context.Connection.RemoteIpAddress?.ToString());
            }

            await WritePlaybackInfoErrorAsync(context, "NotAllowed").ConfigureAwait(false);
            return true;
        }

        // Stream, HLS or download request: answer at once instead of letting ffmpeg or the file
        // system hang on a missing drive.
        SendMessage(sessionManager, auth, client, message, throttleKey, config, context.Connection.RemoteIpAddress?.ToString());
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync(message.Header + " : " + message.Text, context.RequestAborted).ConfigureAwait(false);
        return true;
    }

    /// <summary>The message as URI-encoded JSON (HTTP header values are ASCII).</summary>
    internal static string EncodeMessageHeader(UserMessage message)
        => Uri.EscapeDataString(JsonSerializer.Serialize(new { header = message.Header, text = message.Text }));

    private static string GetTitle(BaseItem item)
    {
        if (item is Episode episode && !string.IsNullOrEmpty(episode.SeriesName))
        {
            return episode.SeriesName + " – " + episode.Name;
        }

        return item.Name ?? Path.GetFileNameWithoutExtension(item.Path) ?? string.Empty;
    }

    private static async Task<string?> GetMediaSourceIdAsync(HttpContext context, RouteKind kind)
    {
        var request = context.Request;
        string? fromQuery = request.Query["mediaSourceId"];
        if (!string.IsNullOrEmpty(fromQuery) || kind != RouteKind.PlaybackInfo || !HttpMethods.IsPost(request.Method))
        {
            return fromQuery;
        }

        if (request.ContentLength is > 4 * 1024 * 1024 || request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        request.EnableBuffering();
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, default, context.RequestAborted).ConfigureAwait(false);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in doc.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "MediaSourceId", StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        return property.Value.GetString();
                    }
                }
            }
        }
        catch (JsonException)
        {
            // Jellyfin will report the bad body itself.
        }
        finally
        {
            request.Body.Position = 0;
        }

        return null;
    }

    private static async Task WritePlaybackInfoErrorAsync(HttpContext context, string errorCode)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WriteStartArray("MediaSources");
        writer.WriteEndArray();
        // A play session id, even unused: some clients (jellyfin-kodi) read it before the error code.
        writer.WriteString("PlaySessionId", Guid.NewGuid().ToString("N"));
        writer.WriteString("ErrorCode", errorCode);
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted).ConfigureAwait(false);
    }

    private bool MarkMessageSent(string key)
    {
        var now = DateTime.UtcNow;
        if (_lastMessages.TryGetValue(key, out var last) && now - last < MessageThrottle)
        {
            return false;
        }

        _lastMessages[key] = now;
        if (_lastMessages.Count > 500)
        {
            foreach (var entry in _lastMessages.Where(e => now - e.Value > MessageThrottle).ToList())
            {
                _lastMessages.TryRemove(entry.Key, out _);
            }
        }

        return true;
    }

    /// <summary>Returns true when the session can display a message (DisplayMessage over its websocket).</summary>
    private static bool CanDisplayMessages(SessionInfo session)
        => session.SupportsRemoteControl && session.SupportedCommands.Contains(GeneralCommandType.DisplayMessage);

    /// <summary>
    /// Sessions of the same user on the same device. An app may open a second session for its native
    /// player whose DeviceId is the UI's DeviceId followed by the user id (jellyfin-android).
    /// </summary>
    private static bool IsSameDevice(SessionInfo session, AuthorizationInfo auth)
    {
        if (string.IsNullOrEmpty(session.DeviceId) || string.IsNullOrEmpty(auth.DeviceId))
        {
            return false;
        }

        if (auth.User is not null && session.UserId != auth.User.Id)
        {
            return false;
        }

        return auth.DeviceId.StartsWith(session.DeviceId, StringComparison.Ordinal)
            || session.DeviceId.StartsWith(auth.DeviceId, StringComparison.Ordinal);
    }

    private static bool IsCompanionPlayer(ISessionManager sessionManager, AuthorizationInfo auth)
    {
        // jellyfin-android's integrated player: DeviceId = UI DeviceId + user id. Recognised even when
        // the UI's websocket is down (then no session can show the message, but the player must still
        // get a clean "not allowed" answer instead of a 404 that leaves it stuck).
        if (auth.User is not null && !string.IsNullOrEmpty(auth.DeviceId)
            && (auth.DeviceId.EndsWith(auth.User.Id.ToString("D"), StringComparison.OrdinalIgnoreCase)
                || auth.DeviceId.EndsWith(auth.User.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
            && auth.DeviceId.Length > 36)
        {
            return true;
        }

        var sessions = sessionManager.Sessions.Where(s => IsSameDevice(s, auth)).ToList();
        var own = sessions.FirstOrDefault(s => string.Equals(s.DeviceId, auth.DeviceId, StringComparison.Ordinal));
        return (own is null || !CanDisplayMessages(own))
            && sessions.Any(s => !string.Equals(s.DeviceId, auth.DeviceId, StringComparison.Ordinal) && CanDisplayMessages(s));
    }

    private static SessionInfo? FindMessageSession(ISessionManager sessionManager, AuthorizationInfo auth, ClientKind client, string? remoteIp)
    {
        var all = sessionManager.Sessions.ToList();
        var sameDevice = all.Where(s => IsSameDevice(s, auth)).ToList();
        var best = sameDevice
            .OrderByDescending(CanDisplayMessages)
            .ThenByDescending(s => string.Equals(s.DeviceId, auth.DeviceId, StringComparison.Ordinal))
            .ThenByDescending(s => s.LastActivityDate)
            .FirstOrDefault();
        if (best is not null && CanDisplayMessages(best))
        {
            return best;
        }

        // Some add-ons play with another DeviceId than their background service (jellyfin-kodi:
        // "Unknown Device id"). Fall back to a session of the same user, same client family and same
        // address that can display messages.
        if (auth.User is not null && !string.IsNullOrEmpty(remoteIp))
        {
            var fallback = all
                .Where(s => s.UserId == auth.User.Id
                    && CanDisplayMessages(s)
                    && ClientClassifier.Classify(s.Client) == client
                    && SameAddress(s.RemoteEndPoint, remoteIp))
                .OrderByDescending(s => s.LastActivityDate)
                .FirstOrDefault();
            if (fallback is not null)
            {
                return fallback;
            }
        }

        return best;
    }

    private static bool SameAddress(string? endPoint, string remoteIp)
    {
        if (string.IsNullOrEmpty(endPoint))
        {
            return false;
        }

        static string Normalize(string value)
            => System.Net.IPAddress.TryParse(value, out var ip) ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString() : value;

        return string.Equals(Normalize(endPoint), Normalize(remoteIp), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The Android TV app opens its player before asking for the stream and, for a movie, does not close
    /// it when the server refuses: it stays on a black screen. A "Stop" command closes it and brings the
    /// details screen back; it is sent once the message has been shown.
    /// </summary>
    private void ClosePlayerLater(ISessionManager sessionManager, AuthorizationInfo auth, ClientKind client, PluginConfiguration config, string? remoteIp)
    {
        var session = FindMessageSession(sessionManager, auth, client, remoteIp);
        if (session is null || !session.SupportsRemoteControl)
        {
            return;
        }

        var sessionId = session.Id;
        var delay = Math.Clamp(config.AndroidTvClosePlayerDelayMs, 0, 10000);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delay).ConfigureAwait(false);
                await sessionManager.SendPlaystateCommand(null!, sessionId, new PlaystateRequest { Command = PlaystateCommand.Stop }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyUSB: could not close the player of session {Session}", sessionId);
            }
        });
    }

    private void SendMessage(
        ISessionManager sessionManager,
        AuthorizationInfo auth,
        ClientKind client,
        UserMessage message,
        string throttleKey,
        PluginConfiguration config,
        string? remoteIp)
    {
        if (!MarkMessageSent(throttleKey))
        {
            return;
        }

        var session = FindMessageSession(sessionManager, auth, client, remoteIp);
        if (session is null)
        {
            _logger.LogInformation("JellyUSB: aucune session capable d'afficher le message pour l'appareil {Device} ({Client}), message non envoyé", auth.DeviceId, auth.Client);
            return;
        }

        var command = new MessageCommand
        {
            Header = message.Header,
            Text = message.Text,
            // Kodi needs a duration for its notification; the web shows a dialog when there is none.
            TimeoutMs = client == ClientKind.Kodi ? 10000 : null
        };

        var delay = client switch
        {
            ClientKind.AndroidTv => Math.Max(0, config.AndroidTvMessageDelayMs),
            ClientKind.WebUi => Math.Max(0, config.WebMessageDelayMs),
            _ => 0
        };
        var sessionId = session.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                if (delay > 0)
                {
                    await Task.Delay(delay).ConfigureAwait(false);
                }

                await sessionManager.SendMessageCommand(null!, sessionId, command, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "JellyUSB: could not send the message to session {Session}", sessionId);
            }
        });
    }
}
