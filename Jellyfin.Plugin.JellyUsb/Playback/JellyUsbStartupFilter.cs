using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.JellyUsb.Playback;

/// <summary>Inserts <see cref="PlaybackGuardMiddleware"/> at the start of the HTTP pipeline.</summary>
public class JellyUsbStartupFilter : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<Web.WebClientInjectionMiddleware>();
            app.UseMiddleware<PlaybackGuardMiddleware>();
            app.UseMiddleware<DetailsMediaIdMiddleware>();
            next(app);
        };
    }
}
