using System;
using System.Globalization;
using System.Linq;
using System.Security;

namespace Jellyfin.Plugin.JellyUsb.Web;

/// <summary>
/// Draws the background of a MediaID (16:9 SVG): a USB key with the MediaID on its label, on a
/// gradient whose colour comes from the MediaID, so each medium is recognisable at a glance.
/// </summary>
public static class MediaBackground
{
    /// <summary>The hue (0-359) of a MediaID: from its number when it ends with one (MOVIES02), else from its name.</summary>
    public static int Hue(string mediaId)
    {
        var digits = new string(mediaId.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        if (digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
        {
            return (205 + (n * 47)) % 360; // neighbours get clearly different colours
        }

        var hash = 0;
        foreach (var c in mediaId)
        {
            hash = unchecked((hash * 31) + c);
        }

        return (int)((uint)hash % 360);
    }

    /// <summary>The SVG document for <paramref name="mediaId"/>.</summary>
    public static string Build(string mediaId)
    {
        var h = Hue(mediaId);
        var h2 = (h + 40) % 360;
        var name = SecurityElement.Escape(mediaId) ?? string.Empty;
        var digits = new string(mediaId.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
        var mark = SecurityElement.Escape(digits.Length > 0 ? digits : mediaId[..Math.Min(2, mediaId.Length)]) ?? string.Empty;
        var nameSize = Math.Clamp(600 / Math.Max(1, mediaId.Length), 26, 64);
        var labelSize = Math.Clamp(150 / Math.Max(1, mediaId.Length) * 2, 11, 22);

        return string.Create(CultureInfo.InvariantCulture, $$"""
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 360" width="640" height="360" role="img" aria-label="{{name}}">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="hsl({{h}},58%,30%)"/>
      <stop offset="1" stop-color="hsl({{h2}},62%,9%)"/>
    </linearGradient>
    <linearGradient id="body" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="hsl({{h}},70%,62%)"/>
      <stop offset="1" stop-color="hsl({{h}},65%,40%)"/>
    </linearGradient>
    <linearGradient id="metal" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#f2f4f7"/>
      <stop offset="1" stop-color="#9aa3ad"/>
    </linearGradient>
  </defs>
  <rect width="640" height="360" fill="url(#bg)"/>
  <g fill="#fff" opacity=".05">
    <path d="M-40 360 L180 0 H260 L40 360Z"/>
    <path d="M120 360 L340 0 H370 L150 360Z"/>
  </g>
  <text x="628" y="350" text-anchor="end" font-family="DejaVu Sans, Arial, sans-serif" font-weight="700" font-size="230" fill="#fff" opacity=".07">{{mark}}</text>
  <g transform="translate(468 168) rotate(-28)">
    <rect x="-34" y="-150" width="68" height="44" rx="4" fill="url(#metal)"/>
    <rect x="-20" y="-140" width="14" height="10" rx="1.5" fill="#56606b"/>
    <rect x="6" y="-140" width="14" height="10" rx="1.5" fill="#56606b"/>
    <rect x="-52" y="-110" width="104" height="230" rx="22" fill="url(#body)" stroke="#000" stroke-opacity=".25" stroke-width="2"/>
    <rect x="-38" y="-80" width="76" height="150" rx="8" fill="#fff" opacity=".93"/>
    <text x="0" y="-5" transform="rotate(90)" text-anchor="middle" dominant-baseline="middle" font-family="DejaVu Sans, Arial, sans-serif" font-weight="700" font-size="{{labelSize}}" fill="hsl({{h}},55%,25%)">{{name}}</text>
    <circle cx="0" cy="96" r="7" fill="#000" opacity=".25"/>
  </g>
  <text x="36" y="292" font-family="DejaVu Sans, Arial, sans-serif" font-weight="700" font-size="{{nameSize}}" fill="#fff">{{name}}</text>
  <text x="38" y="326" font-family="DejaVu Sans, Arial, sans-serif" font-size="20" fill="#fff" opacity=".75">Support USB</text>
</svg>
""");
    }
}
