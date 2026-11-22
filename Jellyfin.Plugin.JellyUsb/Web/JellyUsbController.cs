using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyUsb.Playback;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyUsb.Web;

/// <summary>JellyUSB endpoints used by the web client script.</summary>
[ApiController]
[Route("JellyUsb")]
public class JellyUsbController : ControllerBase
{
    private readonly ActivityService _activity;
    private readonly MediaLibraryService _media;
    private readonly IAuthorizationContext _authContext;
    private readonly ILibraryManager _libraryManager;
    private readonly MediaPresenceChecker _checker;

    public JellyUsbController(ActivityService activity, MediaLibraryService media, IAuthorizationContext authContext, ILibraryManager libraryManager, MediaPresenceChecker checker)
    {
        _activity = activity;
        _media = media;
        _authContext = authContext;
        _libraryManager = libraryManager;
        _checker = checker;
    }

    /// <summary>The script added to jellyfin-web (no secret inside: the dialog headers to recognise).</summary>
    [HttpGet("web.js")]
    [AllowAnonymous]
    [Produces("application/javascript")]
    public IActionResult GetScript()
    {
        var config = Plugin.CurrentConfiguration;
        using var stream = typeof(JellyUsbController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyUsb.Web.jellyusb.js");
        if (stream is null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(stream);
        var settings = JsonSerializer.Serialize(new
        {
            headers = new[] { config.MissingMediaHeader, config.MissingFileHeader },
            closeGenericDialog = config.WebCloseGenericDialog,
            tasksPanel = config.WebTasksPanel,
            mediaView = config.WebMediaView,
            mediaViewName = config.WebMediaViewName,
            mediaButton = config.WebMediaButton,
            mediaButtonName = config.WebMediaButtonName,
            homeCard = config.WebHomeCard,
            detailsMediaId = config.WebDetailsMediaId,
            version = typeof(JellyUsbController).Assembly.GetName().Version?.ToString()
        });
        var script = reader.ReadToEnd().Replace("/*JELLYUSB_SETTINGS*/null", settings, System.StringComparison.Ordinal);
        Response.Headers.CacheControl = "no-cache";
        return Content(script, "application/javascript; charset=utf-8");
    }

    /// <summary>Running tasks and what keeps each USB medium busy (administrators only).</summary>
    [HttpGet("Activity")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<Activity> GetActivity() => _activity.GetActivity();

    /// <summary>The USB media of a library, with their number of movies (empty when the library has none).</summary>
    [HttpGet("Libraries/{libraryId}/Media")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LibraryMedia>>> GetLibraryMedia([FromRoute] Guid libraryId)
    {
        var user = (await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null)
        {
            return Unauthorized();
        }

        return Ok(_media.GetMedia(libraryId, user));
    }

    /// <summary>A page of the movies of one medium of a library.</summary>
    [HttpGet("Libraries/{libraryId}/Media/{mediaId}/Items")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<QueryResult<BaseItemDto>>> GetMediaItems(
        [FromRoute] Guid libraryId,
        [FromRoute] string mediaId,
        [FromQuery] int startIndex = 0,
        [FromQuery] int limit = 100,
        [FromQuery] string? sortBy = null)
    {
        var user = (await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null)
        {
            return Unauthorized();
        }

        var newest = string.Equals(sortBy, "DateCreated", StringComparison.OrdinalIgnoreCase);
        var result = _media.GetItems(libraryId, mediaId, user, startIndex, limit, newest);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// The MediaID of a movie and whether its drive is plugged in, for its details page (204 when the
    /// movie is not on a USB medium). Checked like a playback: an absent automount is not woken up.
    /// </summary>
    [HttpGet("Items/{itemId}/Media")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ItemMedia>> GetItemMedia([FromRoute] Guid itemId)
    {
        var user = (await _authContext.GetAuthorizationInfo(HttpContext).ConfigureAwait(false)).User;
        if (user is null)
        {
            return Unauthorized();
        }

        if (_libraryManager.GetItemById(itemId) is not { } item || !item.IsVisible(user))
        {
            return NotFound();
        }

        var config = Plugin.CurrentConfiguration;
        var presence = item.IsFolder ? default : _checker.Check(item.Path, config.BasePath);
        var text = ItemMedia.BuildText(config, presence);
        if (text is null || presence.MediaId is null)
        {
            return NoContent();
        }

        var library = _libraryManager.GetCollectionFolders(item).OfType<CollectionFolder>()
            .FirstOrDefault(f => f.CollectionType == Jellyfin.Data.Enums.CollectionType.movies);
        return Ok(new ItemMedia(presence.MediaId, presence.Status.ToString(), text, library?.Id));
    }

    /// <summary>The plugin image (PNG), for the "MediaID" tile of My media (anonymous, like Jellyfin's images).</summary>
    [HttpGet("Image")]
    [AllowAnonymous]
    [Produces("image/png")]
    public IActionResult GetPluginImage()
    {
        Response.Headers.CacheControl = "public, max-age=86400";
        return File(MediaImages.PluginPng(), "image/png");
    }

    /// <summary>The background image of a MediaID (anonymous, like Jellyfin's own images: it only shows the name).</summary>
    [HttpGet("Media/{mediaId}/Background")]
    [AllowAnonymous]
    [Produces("image/svg+xml")]
    public IActionResult GetMediaBackground([FromRoute] string mediaId)
    {
        if (string.IsNullOrWhiteSpace(mediaId) || mediaId.Length > 64)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "public, max-age=86400";
        return Content(MediaBackground.Build(mediaId), "image/svg+xml; charset=utf-8");
    }
}

/// <summary>The MediaID of a movie and the state of its drive, for the web details page.</summary>
/// <param name="MediaId">The MediaID (folder name of the drive).</param>
/// <param name="Status">Present, MediaAbsent or FileMissing.</param>
/// <param name="Text">The line to show (the details texts of the settings, without their leading emoji; **bold** kept).</param>
/// <param name="LibraryId">The movie library holding the movie, for the link to the MediaID view.</param>
public record ItemMedia(string MediaId, string Status, string Text, Guid? LibraryId)
{
    /// <summary>The line for <paramref name="presence"/>, or null when the movie is not on a USB medium.</summary>
    public static string? BuildText(Configuration.PluginConfiguration config, PresenceResult presence)
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

        // the web page draws its own coloured dot instead of the emoji
        var text = System.Text.RegularExpressions.Regex.Replace(template, "^[\\s\\p{So}\\p{Cs}\\uFE0F\\u200D]+", string.Empty);
        return text.Replace("{mediaId}", presence.MediaId, StringComparison.Ordinal).Trim();
    }
}
