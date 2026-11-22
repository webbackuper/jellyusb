using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyUsb.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>
/// Shows the MediaID of a movie, and whether its drive is plugged in, on the details screen of the
/// Android TV app: a first line is added to the overview in the answer to GET /Items/{id} (the app
/// renders the overview as markdown). Nothing is stored: the database and the NFO files are untouched,
/// and other clients get the usual answer.
/// </summary>
public partial class DetailsMediaIdMiddleware
{
    private const string GuidPattern = "[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}";

    private readonly RequestDelegate _next;
    private readonly ILogger<DetailsMediaIdMiddleware> _logger;
    private readonly MediaPresenceChecker _checker;

    public DetailsMediaIdMiddleware(RequestDelegate next, ILogger<DetailsMediaIdMiddleware> logger, MediaPresenceChecker checker)
    {
        _next = next;
        _logger = logger;
        _checker = checker;
    }

    [GeneratedRegex("/(?:Users/" + GuidPattern + "/)?Items/(?<id>" + GuidPattern + ")/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemRoute();

    public async Task InvokeAsync(HttpContext context, ILibraryManager libraryManager, IAuthorizationContext authorizationContext)
    {
        var config = Plugin.CurrentConfiguration;
        var request = context.Request;
        var match = config.ShowMediaIdOnTvDetails && HttpMethods.IsGet(request.Method) && request.Path.Value is { } path
            ? ItemRoute().Match(path)
            : Match.Empty;
        if (!match.Success
            || !Guid.TryParse(match.Groups["id"].Value, out var itemId)
            || libraryManager.GetItemById(itemId) is not { } item
            || item.IsFolder
            || MediaPresenceChecker.GetMediaId(item.Path, config.BasePath) is null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Only the Android TV app; read from the auth header without touching the request.
        var auth = await authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
        if (!auth.IsAuthenticated || ClientClassifier.Classify(auth.Client) != ClientKind.AndroidTv)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // A plain, complete copy: no compression, no 304.
        request.Headers.Remove("Accept-Encoding");
        request.Headers.Remove("If-None-Match");
        request.Headers.Remove("If-Modified-Since");

        var original = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = original;
        }

        var bytes = buffer.ToArray();
        if (context.Response.StatusCode == StatusCodes.Status200OK
            && context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var presence = _checker.Check(item.Path, config.BasePath);
                var line = BuildLine(config, presence);
                var decorated = line is null ? null : AddToOverview(Encoding.UTF8.GetString(bytes), line);
                if (decorated is not null)
                {
                    bytes = Encoding.UTF8.GetBytes(decorated);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Never break the details screen because of JellyUSB.
                _logger.LogWarning(ex, "JellyUSB: MediaID not added to the details of {Item}", item.Id);
            }
        }

        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>The line for <paramref name="presence"/>, or null when the item is not on a USB medium.</summary>
    public static string? BuildLine(PluginConfiguration config, PresenceResult presence)
    {
        var template = presence.Status switch
        {
            PresenceStatus.Present => config.TvDetailsPresentText,
            PresenceStatus.MediaAbsent => config.TvDetailsAbsentText,
            PresenceStatus.FileMissing => config.TvDetailsFileMissingText,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(template) || presence.MediaId is null)
        {
            return null;
        }

        // the MediaID is a folder name: keep markdown and HTML out of it
        var mediaId = Regex.Replace(presence.MediaId, "[\\\\`*_\\[\\]<>#|~]", m => "\\" + m.Value);
        return template.Replace("{mediaId}", mediaId, StringComparison.Ordinal).Trim();
    }

    /// <summary>
    /// Puts <paramref name="line"/> before the overview of the item in <paramref name="json"/> (a
    /// BaseItemDto), with a blank line between them. Returns null when the JSON is not an item.
    /// </summary>
    public static string? AddToOverview(string json, string line)
    {
        if (JsonNode.Parse(json) is not JsonObject dto || dto["Id"] is null)
        {
            return null;
        }

        var overview = dto["Overview"]?.GetValue<string>();
        dto["Overview"] = string.IsNullOrWhiteSpace(overview) ? line : line + "\n\n" + overview;
        return dto.ToJsonString();
    }
}
