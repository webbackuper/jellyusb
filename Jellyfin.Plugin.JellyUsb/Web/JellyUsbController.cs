using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
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

    public JellyUsbController(ActivityService activity, MediaLibraryService media, IAuthorizationContext authContext)
    {
        _activity = activity;
        _media = media;
        _authContext = authContext;
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
