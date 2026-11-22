using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using Jellyfin.Plugin.JellyUsb.Playback;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.Web;

/// <summary>
/// A "MediaID" tile in "My media" of the Android TV app, which has no web script: the app gets one more
/// library, of type "collections", where each USB medium is a collection holding its movies. Nothing is
/// stored: the library and the collections exist only in the answers to the TV app (ids derived from the
/// MediaID); the movies are the real ones.
/// </summary>
public partial class TvMediaViewMiddleware
{
    private const string GuidPattern = "[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}";

    /// <summary>The id of the virtual library.</summary>
    public static readonly Guid ViewId = StableGuid("JellyUSB:view");

    private readonly RequestDelegate _next;
    private readonly ILogger<TvMediaViewMiddleware> _logger;
    private readonly MediaLibraryService _media;

    public TvMediaViewMiddleware(RequestDelegate next, ILogger<TvMediaViewMiddleware> logger, MediaLibraryService media)
    {
        _next = next;
        _logger = logger;
        _media = media;
    }

    [GeneratedRegex("/(?:UserViews|Users/" + GuidPattern + "/Views)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ViewsRoute();

    [GeneratedRegex("/(?:Users/" + GuidPattern + "/)?Items/(?<id>" + GuidPattern + ")/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemRoute();

    [GeneratedRegex("/(?:Users/" + GuidPattern + "/)?Items/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ItemsRoute();

    [GeneratedRegex("/Items/(?<id>" + GuidPattern + ")/Images/(?<type>[A-Za-z]+)(?:/\\d+)?/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ImageRoute();

    /// <summary>The id of the collection of <paramref name="mediaId"/>.</summary>
    public static Guid MediaGuid(string mediaId) => StableGuid("JellyUSB:media:" + mediaId);

    public async Task InvokeAsync(HttpContext context, IAuthorizationContext authorizationContext, IServerApplicationHost appHost)
    {
        var config = Plugin.CurrentConfiguration;
        var request = context.Request;
        var path = request.Path.Value ?? string.Empty;
        if (!config.TvMediaView || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method)))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Images: fetched without authentication, like Jellyfin's own; they only show a disk name.
        var image = ImageRoute().Match(path);
        if (image.Success && Guid.TryParse(image.Groups["id"].Value, out var imageId) && IsVirtual(imageId, out var imageMedia))
        {
            var width = int.TryParse(request.Query["maxWidth"], out var w) ? w : int.TryParse(request.Query["fillWidth"], out var fw) ? fw : 960;
            var png = imageMedia is null ? MediaImages.PluginPng() : MediaImages.BackgroundPng(imageMedia, width);
            context.Response.ContentType = "image/png";
            context.Response.Headers.CacheControl = "public, max-age=86400";
            context.Response.ContentLength = png.Length;
            if (!HttpMethods.IsHead(request.Method))
            {
                await context.Response.Body.WriteAsync(png, context.RequestAborted).ConfigureAwait(false);
            }

            return;
        }

        var isViews = ViewsRoute().IsMatch(path);
        var item = ItemRoute().Match(path);
        var isItem = item.Success && Guid.TryParse(item.Groups["id"].Value, out var itemId) && IsVirtual(itemId, out _);
        var parent = request.Query["ParentId"].ToString();
        var isItems = ItemsRoute().IsMatch(path) && Guid.TryParse(parent, out var parentId) && IsVirtual(parentId, out _);
        if (!isViews && !isItem && !isItems)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var auth = await authorizationContext.GetAuthorizationInfo(context).ConfigureAwait(false);
        if (!auth.IsAuthenticated || auth.User is null || ClientClassifier.Classify(auth.Client) != ClientKind.AndroidTv)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        try
        {
            if (isViews)
            {
                await AddViewAsync(context, auth.User, appHost.SystemId, config).ConfigureAwait(false);
                return;
            }

            var all = _media.GetAllMedia(auth.User);
            if (isItem)
            {
                var id = Guid.Parse(item.Groups["id"].Value);
                var dto = id == ViewId
                    ? ViewDto(appHost.SystemId, config, all.Count)
                    : all.Where(m => MediaGuid(m.Media.MediaId) == id).Select(m => MediaDto(appHost.SystemId, m.Media, config)).FirstOrDefault();
                if (dto is null)
                {
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                await WriteJsonAsync(context, dto).ConfigureAwait(false);
                return;
            }

            await WriteItemsAsync(context, Guid.Parse(parent), auth.User, all, appHost.SystemId, config).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && !context.Response.HasStarted)
        {
            _logger.LogWarning(ex, "JellyUSB: MediaID view not answered for {Path}", path);
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }

    private static bool IsVirtual(Guid id, out string? mediaId)
    {
        mediaId = null;
        if (id == ViewId)
        {
            return true;
        }

        mediaId = KnownMedia.TryGetValue(id, out var known) ? known : null;
        return mediaId is not null;
    }

    // MediaID by collection id: every MediaID the views have listed, plus MOVIES00-MOVIES99.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> KnownMedia = new(
        Enumerable.Range(0, 100).Select(i => "MOVIES" + i.ToString("00", System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(MediaGuid, m => m));

    private async Task AddViewAsync(HttpContext context, User user, string serverId, Configuration.PluginConfiguration config)
    {
        var all = _media.GetAllMedia(user);
        foreach (var m in all)
        {
            KnownMedia[MediaGuid(m.Media.MediaId)] = m.Media.MediaId;
        }

        // the regular answer, read in full, then the tile added after the libraries
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
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
        if (all.Count > 0 && context.Response.StatusCode == StatusCodes.Status200OK
            && JsonNode.Parse(bytes) is JsonObject result && result["Items"] is JsonArray items)
        {
            items.Add(JsonNode.Parse(JsonSerializer.Serialize(ViewDto(serverId, config, all.Count), JsonDefaults.Options)));
            if (result["TotalRecordCount"] is not null)
            {
                result["TotalRecordCount"] = items.Count;
            }

            bytes = Encoding.UTF8.GetBytes(result.ToJsonString());
        }

        context.Response.ContentLength = bytes.Length;
        await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }

    private async Task WriteItemsAsync(HttpContext context, Guid parentId, User user, IReadOnlyList<(Guid LibraryId, LibraryMedia Media)> all, string serverId, Configuration.PluginConfiguration config)
    {
        var query = context.Request.Query;
        var start = int.TryParse(query["StartIndex"], out var s) ? Math.Max(0, s) : 0;
        var limit = int.TryParse(query["Limit"], out var l) ? Math.Clamp(l, 1, 500) : 100;
        var include = List(query["IncludeItemTypes"]);
        var exclude = List(query["ExcludeItemTypes"]);

        QueryResult<BaseItemDto> result;
        if (parentId == ViewId)
        {
            // the media, as collections
            var wanted = (include.Count == 0 || include.Contains("BoxSet")) && !exclude.Contains("BoxSet");
            var media = wanted ? all.Select(m => MediaDto(serverId, m.Media, config)).ToList() : [];
            result = new QueryResult<BaseItemDto>(start, media.Count, media.Skip(start).Take(limit).ToList());
        }
        else
        {
            // the movies of one medium (the app also asks for its series and "other": none)
            var entry = all.FirstOrDefault(m => MediaGuid(m.Media.MediaId) == parentId);
            var wanted = (include.Count == 0 || include.Contains("Movie")) && !exclude.Contains("Movie");
            var fields = List(query["Fields"])
                .Select(f => Enum.TryParse<ItemFields>(f, true, out var field) ? field : (ItemFields?)null)
                .Where(f => f is not null).Select(f => f!.Value).ToList();
            var page = entry.Media is not null && wanted
                ? _media.GetItems(entry.LibraryId, entry.Media.MediaId, user, start, limit, false, fields)
                : null;
            result = page ?? new QueryResult<BaseItemDto>(start, 0, []);
        }

        await WriteJsonAsync(context, result).ConfigureAwait(false);
    }

    private static BaseItemDto ViewDto(string serverId, Configuration.PluginConfiguration config, int count) => new()
    {
        Name = string.IsNullOrWhiteSpace(config.WebMediaViewName) ? "MediaID" : config.WebMediaViewName,
        ServerId = serverId,
        Id = ViewId,
        Type = BaseItemKind.CollectionFolder,
        CollectionType = CollectionType.boxsets,
        IsFolder = true,
        DisplayPreferencesId = ViewId.ToString("N"),
        ChildCount = count,
        PrimaryImageAspectRatio = 16d / 9d,
        ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = ImageTag() },
        BackdropImageTags = [],
        LocationType = LocationType.Virtual
    };

    private static BaseItemDto MediaDto(string serverId, LibraryMedia media, Configuration.PluginConfiguration config)
    {
        var id = MediaGuid(media.MediaId);
        var films = media.MovieCount + (media.MovieCount > 1 ? " films" : " film");
        return new BaseItemDto
        {
            Name = media.MediaId,
            SortName = media.MediaId,
            ServerId = serverId,
            Id = id,
            Type = BaseItemKind.BoxSet,
            IsFolder = true,
            ChildCount = media.MovieCount,
            Overview = MediaOverview(media.MediaId, media.Present, films),
            DisplayPreferencesId = id.ToString("N"),
            PrimaryImageAspectRatio = 16d / 9d,
            ImageTags = new Dictionary<ImageType, string> { [ImageType.Primary] = ImageTag(), [ImageType.Thumb] = ImageTag() },
            BackdropImageTags = [ImageTag()],
            LocationType = LocationType.Virtual,
            UserData = new UserItemDataDto { Key = id.ToString("N"), ItemId = id, Played = false, PlayCount = 0, PlaybackPositionTicks = 0, IsFavorite = false }
        };
    }

    /// <summary>The text of the collection of a medium (markdown, the TV app renders it).</summary>
    internal static string MediaOverview(string mediaId, bool present, string films)
    {
        var name = Regex.Replace(mediaId, "[\\\\`*_\\[\\]<>#|~]", m => "\\" + m.Value);
        return present
            ? $"🟢 Disque **{name}** branché · {films}"
            : $"🔴 Disque **{name}** absent : branchez-le pour lire ses {films}";
    }

    // changes with each version, so the TV app downloads redrawn images
    private static string ImageTag() => "jellyusb" + typeof(TvMediaViewMiddleware).Assembly.GetName().Version;

    private static HashSet<string> List(string? value)
        => new((value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.OrdinalIgnoreCase);

    private static async Task WriteJsonAsync<T>(HttpContext context, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonDefaults.Options);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private static Guid StableGuid(string text) => new(MD5.HashData(Encoding.UTF8.GetBytes(text)));
}
