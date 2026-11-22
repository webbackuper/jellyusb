using System;
using System.Collections.Concurrent;
using System.IO;
using SkiaSharp;
using Svg.Skia;

namespace Jellyfin.Plugin.JellyUsb.Web;

/// <summary>
/// PNG images for the apps that cannot show SVG (the Android TV app): the MediaID backgrounds drawn by
/// <see cref="MediaBackground"/>, rasterised with the SVG library Jellyfin ships, and the plugin image.
/// </summary>
public static class MediaImages
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    /// <summary>The background of <paramref name="mediaId"/> as a PNG, <paramref name="width"/> pixels wide (16:9).</summary>
    public static byte[] BackgroundPng(string mediaId, int width = 960)
    {
        width = Math.Clamp(width, 160, 1920);
        return Cache.GetOrAdd(mediaId + "|" + width, _ => Render(MediaBackground.Build(mediaId), width));
    }

    /// <summary>The plugin image (assets/jellyusb.png, 1280x720).</summary>
    public static byte[] PluginPng()
        => Cache.GetOrAdd("plugin", _ =>
        {
            using var stream = typeof(MediaImages).Assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyUsb.jellyusb.png")
                ?? throw new InvalidOperationException("plugin image missing");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        });

    internal static byte[] Render(string svgText, int width)
    {
        using var svg = new SKSvg();
        using (var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(svgText)))
        {
            svg.Load(input);
        }

        var picture = svg.Picture ?? throw new InvalidOperationException("SVG not rendered");
        var bounds = picture.CullRect;
        var scale = width / bounds.Width;
        var height = (int)Math.Round(bounds.Height * scale);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        return data.ToArray();
    }
}
