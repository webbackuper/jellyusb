using System.IO;
using System.Text.Json;
using MediaBrowser.Common.Api;
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

    public JellyUsbController(ActivityService activity)
    {
        _activity = activity;
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
            tasksPanel = config.WebTasksPanel
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
}
