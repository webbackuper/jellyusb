#!/usr/bin/env python3
"""Draws the JellyUSB plugin image (16:9, shown in Dashboard > Plugins).

    python3 assets/make-image.py            -> assets/jellyusb.png

Three USB drives fanned out: two plugged in (one with a green check), one absent (dashed,
amber badge). Drawn at 2x and downscaled for clean edges. Needs Pillow.
"""
import os
from PIL import Image, ImageDraw, ImageFilter, ImageFont

W, H = 1280, 720
S = 2  # supersampling
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "jellyusb.png")

FONT_TITLE = "/usr/share/fonts/truetype/ubuntu/Ubuntu-B.ttf"
FONT_TEXT = "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf"
FONT_MONO = "/usr/share/fonts/truetype/noto/NotoSansMono-Bold.ttf"

PURPLE = (170, 92, 195)
BLUE = (0, 164, 220)


def s(v):
    return int(round(v * S))


def hexrgb(h):
    h = h.lstrip("#")
    return tuple(int(h[i:i + 2], 16) for i in (0, 2, 4))


def gradient(size, top, bottom, horizontal=False):
    w, h = size
    base = Image.linear_gradient("L").resize((256, 256))
    if horizontal:
        base = base.rotate(90, expand=True)
    mask = base.resize((w, h))
    a = Image.new("RGBA", (w, h), top + (255,))
    b = Image.new("RGBA", (w, h), bottom + (255,))
    return Image.composite(b, a, mask)


def glow(canvas, center, radius, color, alpha):
    layer = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    cx, cy = center
    d.ellipse((cx - radius, cy - radius, cx + radius, cy + radius), fill=color + (alpha,))
    layer = layer.filter(ImageFilter.GaussianBlur(radius * 0.45))
    canvas.alpha_composite(layer)


def rounded_mask(size, radius):
    m = Image.new("L", size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size[0] - 1, size[1] - 1), radius=radius, fill=255)
    return m


def dashed_rounded_rect(draw, box, radius, color, width, dash, gap):
    """Dashed outline of a rounded rectangle (straight sides dashed, corners solid arcs)."""
    x0, y0, x1, y1 = box
    r = radius
    for (a, b, fixed, vertical) in ((x0 + r, x1 - r, y0, False), (x0 + r, x1 - r, y1, False),
                                     (y0 + r, y1 - r, x0, True), (y0 + r, y1 - r, x1, True)):
        p = a
        while p < b:
            q = min(p + dash, b)
            if vertical:
                draw.line((fixed, p, fixed, q), fill=color, width=width)
            else:
                draw.line((p, fixed, q, fixed), fill=color, width=width)
            p = q + gap
    for (cx, cy, start) in ((x0 + r, y0 + r, 180), (x1 - r, y0 + r, 270), (x1 - r, y1 - r, 0), (x0 + r, y1 - r, 90)):
        draw.arc((cx - r, cy - r, cx + r, cy + r), start, start + 90, fill=color, width=width)


def drive(label, top, bottom, ghost=False):
    """One USB stick, connector up. Returns an RGBA image."""
    bw, bh = s(176), s(340)          # body
    cw, ch = s(112), s(120)          # connector
    pad = s(30)
    img = Image.new("RGBA", (bw + 2 * pad, ch + bh + 2 * pad), (0, 0, 0, 0))
    ox, oy = pad, pad
    cx0 = ox + (bw - cw) // 2

    if ghost:
        d = ImageDraw.Draw(img)
        faint = Image.new("RGBA", (bw, bh), top + (40,))
        img.alpha_composite(Image.composite(faint, Image.new("RGBA", (bw, bh), (0, 0, 0, 0)), rounded_mask((bw, bh), s(34))), (ox, oy + ch))
        line = top + (230,)
        dashed_rounded_rect(d, (cx0, oy, cx0 + cw, oy + ch - s(4)), s(10), line, s(4), s(16), s(11))
        dashed_rounded_rect(d, (ox, oy + ch, ox + bw, oy + ch + bh), s(34), line, s(5), s(20), s(13))
    else:
        # connector: brushed metal
        metal = gradient((cw, ch + s(14)), (226, 230, 238), (150, 158, 176), horizontal=True)
        img.alpha_composite(Image.composite(metal, Image.new("RGBA", metal.size, (0, 0, 0, 0)), rounded_mask(metal.size, s(10))), (cx0, oy))
        d = ImageDraw.Draw(img)
        for hx in (cx0 + s(24), cx0 + cw - s(24) - s(22)):
            d.rounded_rectangle((hx, oy + s(30), hx + s(22), oy + s(56)), radius=s(3), fill=(70, 78, 98, 255))
        # body
        body = gradient((bw, bh), top, bottom)
        img.alpha_composite(Image.composite(body, Image.new("RGBA", (bw, bh), (0, 0, 0, 0)), rounded_mask((bw, bh), s(34))), (ox, oy + ch))
        # gloss on the left
        gloss = Image.new("RGBA", (bw, bh), (0, 0, 0, 0))
        ImageDraw.Draw(gloss).rounded_rectangle((s(14), s(14), s(62), bh - s(70)), radius=s(22), fill=(255, 255, 255, 48))
        img.alpha_composite(gloss.filter(ImageFilter.GaussianBlur(s(4))), (ox, oy + ch))
        d = ImageDraw.Draw(img)
        # lanyard hole
        hx, hy = ox + bw // 2, oy + ch + bh - s(36)
        d.ellipse((hx - s(13), hy - s(13), hx + s(13), hy + s(13)), fill=(0, 0, 0, 0), outline=(255, 255, 255, 90), width=s(4))

    # label
    d = ImageDraw.Draw(img)
    lx0, ly0 = ox + s(14), oy + ch + s(112)
    lx1, ly1 = ox + bw - s(14), ly0 + s(64)
    font = ImageFont.truetype(FONT_MONO, s(27))
    if ghost:
        d.rounded_rectangle((lx0, ly0, lx1, ly1), radius=s(10), outline=top + (200,), width=s(3))
        fill = top + (235,)
    else:
        d.rounded_rectangle((lx0, ly0, lx1, ly1), radius=s(10), fill=(246, 247, 252, 255))
        fill = (32, 30, 52, 255)
    tw = d.textlength(label, font=font)
    d.text(((lx0 + lx1 - tw) / 2, (ly0 + ly1) / 2), label, font=font, fill=fill, anchor="lm")
    if not ghost:
        # activity LED
        lx, ly = ox + bw // 2, oy + ch + s(60)
        led = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ImageDraw.Draw(led).ellipse((lx - s(16), ly - s(16), lx + s(16), ly + s(16)), fill=(120, 255, 170, 120))
        img.alpha_composite(led.filter(ImageFilter.GaussianBlur(s(8))))
        d = ImageDraw.Draw(img)
        d.ellipse((lx - s(7), ly - s(7), lx + s(7), ly + s(7)), fill=(140, 255, 185, 255))
    return img


def badge(kind):
    r = s(38)
    img = Image.new("RGBA", (2 * r + s(30), 2 * r + s(30)), (0, 0, 0, 0))
    c = img.size[0] // 2
    sh = Image.new("RGBA", img.size, (0, 0, 0, 0))
    ImageDraw.Draw(sh).ellipse((c - r, c - r + s(6), c + r, c + r + s(6)), fill=(0, 0, 0, 140))
    img.alpha_composite(sh.filter(ImageFilter.GaussianBlur(s(6))))
    d = ImageDraw.Draw(img)
    if kind == "ok":
        d.ellipse((c - r, c - r, c + r, c + r), fill=(46, 204, 113, 255), outline=(255, 255, 255, 255), width=s(5))
        d.line((c - s(17), c + s(1), c - s(5), c + s(14), c + s(19), c - s(13)), fill=(255, 255, 255, 255), width=s(8), joint="curve")
    else:
        d.ellipse((c - r, c - r, c + r, c + r), fill=(245, 166, 35, 255), outline=(255, 255, 255, 255), width=s(5))
        font = ImageFont.truetype(FONT_TITLE, s(50))
        d.text((c, c + s(2)), "!", font=font, fill=(255, 255, 255, 255), anchor="mm")
    return img


def place(canvas, sprite, center, angle, shadow=True):
    rot = sprite.rotate(angle, resample=Image.BICUBIC, expand=True)
    x, y = int(center[0] - rot.size[0] / 2), int(center[1] - rot.size[1] / 2)
    if shadow:
        alpha = rot.split()[3].point(lambda a: min(255, a) * 0.55)
        sh = Image.new("RGBA", rot.size, (5, 5, 20, 0))
        sh.putalpha(alpha)
        sh = sh.filter(ImageFilter.GaussianBlur(s(16)))
        canvas.alpha_composite(sh, (x + s(10), y + s(26)))
    canvas.alpha_composite(rot, (x, y))
    return rot, (x, y)


def main():
    canvas = gradient((s(W), s(H)), hexrgb("#0b0e22"), hexrgb("#1a1238"))
    glow(canvas, (s(930), s(300)), s(430), PURPLE, 95)
    glow(canvas, (s(1150), s(620)), s(330), BLUE, 70)
    glow(canvas, (s(220), s(640)), s(300), BLUE, 40)

    # subtle grid of dots
    dots = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    dd = ImageDraw.Draw(dots)
    for gx in range(0, s(W), s(32)):
        for gy in range(0, s(H), s(32)):
            dd.ellipse((gx, gy, gx + s(2), gy + s(2)), fill=(255, 255, 255, 14))
    canvas.alpha_composite(dots)

    # drives: absent one at the back right, then left, then the front one
    place(canvas, drive("MOVIES02", (90, 220, 230), (90, 220, 230), ghost=True), (s(1112), s(372)), -17, shadow=False)
    place(canvas, drive("MOVIES00", hexrgb("#c07bd6"), hexrgb("#5d2386")), (s(772), s(386)), 17)
    place(canvas, drive("MOVIES01", hexrgb("#1fb8ee"), hexrgb("#0b4f93")), (s(944), s(350)), 0)

    canvas.alpha_composite(badge("ok"), (s(1000), s(108)))
    canvas.alpha_composite(badge("warn"), (s(1168), s(112)))

    # title
    d = ImageDraw.Draw(canvas)
    title = ImageFont.truetype(FONT_TITLE, s(118))
    sub = ImageFont.truetype(FONT_TEXT, s(36))
    d.text((s(84), s(300)), "Jelly", font=title, fill=(255, 255, 255, 255), anchor="ls")
    jw = d.textlength("Jelly", font=title)
    # "USB" with the Jellyfin purple -> blue gradient
    usb_w = int(d.textlength("USB", font=title)) + s(10)
    grad = gradient((usb_w, s(150)), PURPLE, BLUE, horizontal=True)
    mask = Image.new("L", grad.size, 0)
    ImageDraw.Draw(mask).text((0, s(122)), "USB", font=title, fill=255, anchor="ls")
    canvas.paste(grad, (int(s(84) + jw), s(300) - s(122)), mask)
    # accent bar
    bar = gradient((s(120), s(8)), PURPLE, BLUE, horizontal=True)
    canvas.paste(bar, (s(88), s(334)), rounded_mask(bar.size, s(4)))
    d.text((s(86), s(410)), "Vos films, disque par disque.", font=sub, fill=(214, 218, 236, 255), anchor="ls")
    small = ImageFont.truetype(FONT_TEXT, s(26))
    d.text((s(86), s(460)), "Vérifie le support avant la lecture", font=small, fill=(160, 166, 196, 255), anchor="ls")
    d.text((s(86), s(498)), "et répare les titres accentués.", font=small, fill=(160, 166, 196, 255), anchor="ls")

    canvas.resize((W, H), Image.LANCZOS).convert("RGB").save(OUT, optimize=True)
    print("written", OUT)


if __name__ == "__main__":
    main()
