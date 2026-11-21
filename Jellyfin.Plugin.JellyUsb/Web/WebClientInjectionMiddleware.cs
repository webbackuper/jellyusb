using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyUsb.Web;

/// <summary>
/// Adds the JellyUSB script to jellyfin-web's index.html as it is served (the files on disk are not
/// touched). The script closes jellyfin-web's generic "Playback error" dialog when the JellyUSB message
/// is shown, and adds a "running now" panel to the Scheduled Tasks page.
/// </summary>
public partial class WebClientInjectionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<WebClientInjectionMiddleware> _logger;

    public WebClientInjectionMiddleware(RequestDelegate next, ILogger<WebClientInjectionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    [GeneratedRegex("/web/(index\\.html)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IndexRoute();

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        if (!Plugin.CurrentConfiguration.EnableWebEnhancements
            || !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            || request.Path.Value is not { } path
            || !IndexRoute().IsMatch(path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Ask the inner pipeline for a plain, complete copy: no compression, no 304.
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
            && context.Response.ContentType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true)
        {
            var html = Encoding.UTF8.GetString(bytes);
            var close = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (close >= 0 && !html.Contains("JellyUsb/web.js", StringComparison.Ordinal))
            {
                var version = typeof(WebClientInjectionMiddleware).Assembly.GetName().Version?.ToString() ?? "1";
                html = html.Insert(close, "<script defer=\"defer\" src=\"../JellyUsb/web.js?v=" + version + "\"></script>");
                bytes = Encoding.UTF8.GetBytes(html);
            }
            else if (close < 0)
            {
                _logger.LogDebug("JellyUSB: index.html without </body>, script not added");
            }
        }

        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(request.Method))
        {
            await original.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }
}
