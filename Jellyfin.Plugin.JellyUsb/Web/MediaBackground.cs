using System;
using System.Globalization;
using System.Linq;
using System.Security;

namespace Jellyfin.Plugin.JellyUsb.Web;

/// <summary>
/// Draws the background of a MediaID (16:9 SVG): a 2.5" USB disk enclosure with the MediaID on its label, on a
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
        var labelSize = Math.Clamp(150 / Math.Max(1, mediaId.Length), 9, 17);

        return string.Create(CultureInfo.InvariantCulture, $$"""
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 640 360" width="640" height="360" role="img" aria-label="{{name}}">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="hsl({{h}},58%,30%)"/>
      <stop offset="1" stop-color="hsl({{h2}},62%,9%)"/>
    </linearGradient>
    <linearGradient id="body" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="hsl({{h}},60%,34%)"/>
      <stop offset="1" stop-color="hsl({{h}},65%,52%)"/>
    </linearGradient>
    <linearGradient id="face" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="hsl({{h}},75%,68%)"/>
      <stop offset="1" stop-color="hsl({{h}},65%,42%)"/>
    </linearGradient>
    <filter id="glow" x="-1" y="-2" width="3" height="5"><feGaussianBlur stdDeviation="4"/></filter>
  </defs>
  <rect width="640" height="360" fill="url(#bg)"/>
  <g fill="#fff" opacity=".05">
    <path d="M-40 360 L180 0 H260 L40 360Z"/>
    <path d="M120 360 L340 0 H370 L150 360Z"/>
  </g>
  <text x="628" y="350" text-anchor="end" font-family="DejaVu Sans, Arial, sans-serif" font-weight="700" font-size="230" fill="#fff" opacity=".07">{{mark}}</text>
  <g transform="translate(476 150) rotate(-14)">
    <path d="M0 132 C0 196 64 196 74 270" fill="none" stroke="#1c1e2c" stroke-width="12" stroke-linecap="round"/>
    <path d="M-2 132 C-2 196 62 196 72 270" fill="none" stroke="#5c627a" stroke-width="3" stroke-linecap="round"/>
    <rect x="-70" y="-118" width="140" height="236" rx="14" fill="url(#body)" stroke="#000" stroke-opacity=".3" stroke-width="2"/>
    <rect x="-62" y="-110" width="124" height="220" rx="10" fill="url(#face)"/>
    <rect x="-56" y="-104" width="112" height="44" rx="10" fill="#fff" opacity=".12"/>
    <g fill="none" stroke="#fff">
      <circle cx="0" cy="-66" r="26" stroke-opacity=".6" stroke-width="2.5" fill="#fff" fill-opacity=".1"/>
      <circle cx="0" cy="-66" r="14" stroke-opacity=".35" stroke-width="1.5"/>
      <path d="M33 -47 L8 -74" stroke-opacity=".8" stroke-width="4" stroke-linecap="round"/>
    </g>
    <circle cx="0" cy="-66" r="4" fill="#fff" opacity=".8"/>
    <circle cx="33" cy="-47" r="4.5" fill="#fff" opacity=".8"/>
    <rect x="-52" y="-24" width="104" height="40" rx="6" fill="#fff" opacity=".94"/>
    <text x="0" y="-4" text-anchor="middle" dominant-baseline="middle" font-family="DejaVu Sans Mono, monospace" font-weight="700" font-size="{{labelSize}}" fill="hsl({{h}},55%,22%)">{{name}}</text>
    <rect x="-20" y="80" width="40" height="12" rx="6" fill="#8affbe" opacity=".35" filter="url(#glow)"/>
    <rect x="-12" y="84" width="24" height="4" rx="2" fill="#a6ffcc"/>
    <rect x="-20" y="114" width="40" height="24" rx="5" fill="#2a2d3e"/>
    <rect x="-14" y="118" width="28" height="3" rx="1.5" fill="#8c92aa"/>
  </g>
  <text x="36" y="292" font-family="DejaVu Sans, Arial, sans-serif" font-weight="700" font-size="{{nameSize}}" fill="#fff">{{name}}</text>
  <text x="38" y="326" font-family="DejaVu Sans, Arial, sans-serif" font-size="20" fill="#fff" opacity=".75">Support USB</text>
</svg>
""");
    }
}
