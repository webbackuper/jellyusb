#!/usr/bin/env python3
"""Draws the JellyUSB plugin image (16:9, shown in Dashboard > Plugins).

    python3 assets/make-image.py            -> assets/jellyusb.png

Three 2.5" hard disks in USB enclosures, fanned out: two plugged in with their cable (one with a
green check), one absent (dashed, unplugged, amber badge). Drawn at 2x and downscaled for clean edges. Needs Pillow.
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


def bezier(p0, p1, p2, p3, n=48):
    pts = []
    for i in range(n + 1):
        t = i / n
        a, b, c, d = (1 - t) ** 3, 3 * t * (1 - t) ** 2, 3 * t * t * (1 - t), t ** 3
        pts.append((a * p0[0] + b * p1[0] + c * p2[0] + d * p3[0], a * p0[1] + b * p1[1] + c * p2[1] + d * p3[1]))
    return pts


def drive(label, top, bottom, ghost=False, cable_bend=1):
    """One 2.5" hard disk in a USB enclosure, port at the bottom with its cable. Returns an RGBA image."""
    bw, bh = s(226), s(332)          # enclosure (2.5" enclosures are about 80 x 120 mm)
    rad = s(20)
    pad = s(40)
    cable_len = s(190)
    img = Image.new("RGBA", (bw + 2 * pad + s(120), bh + cable_len + 2 * pad), (0, 0, 0, 0))
    ox, oy = pad + s(60), pad
    cx = ox + bw // 2
    plug_w, plug_h = s(64), s(40)
    plug_y = oy + bh - s(4)

    if ghost:
        d = ImageDraw.Draw(img)
        faint = Image.new("RGBA", (bw, bh), top + (38,))
        img.alpha_composite(Image.composite(faint, Image.new("RGBA", (bw, bh), (0, 0, 0, 0)), rounded_mask((bw, bh), rad)), (ox, oy))
        line = top + (230,)
        dashed_rounded_rect(d, (ox, oy, ox + bw, oy + bh), rad, line, s(5), s(20), s(13))
        # empty port, no cable: the disk is not plugged in
        d.rounded_rectangle((cx - s(26), oy + bh - s(22), cx + s(26), oy + bh - s(8)), radius=s(4), outline=line, width=s(3))
    else:
        # cable first (under the plug), curving away
        cable = Image.new("RGBA", img.size, (0, 0, 0, 0))
        cd = ImageDraw.Draw(cable)
        p0 = (cx, plug_y + plug_h - s(4))
        p3 = (cx + cable_bend * s(110), oy + bh + cable_len + pad)
        pts = bezier(p0, (cx, p0[1] + s(90)), (p3[0], p3[1] - s(120)), p3)
        cd.line(pts, fill=(28, 30, 44, 255), width=s(20), joint="curve")
        cd.line([(x - s(3), y) for (x, y) in pts], fill=(92, 98, 122, 255), width=s(5), joint="curve")
        img.alpha_composite(cable)
        # enclosure: rim, then the slightly lighter face
        body = gradient((bw, bh), bottom, top)
        img.alpha_composite(Image.composite(body, Image.new("RGBA", (bw, bh), (0, 0, 0, 0)), rounded_mask((bw, bh), rad)), (ox, oy))
        inset = s(11)
        fw, fh = bw - 2 * inset, bh - 2 * inset
        face = gradient((fw, fh), tuple(min(255, c + 22) for c in top), bottom)
        img.alpha_composite(Image.composite(face, Image.new("RGBA", (fw, fh), (0, 0, 0, 0)), rounded_mask((fw, fh), rad - inset)), (ox + inset, oy + inset))
        # soft gloss across the top of the face
        gloss = Image.new("RGBA", (bw, bh), (0, 0, 0, 0))
        ImageDraw.Draw(gloss).rounded_rectangle((inset + s(10), inset + s(8), bw - inset - s(10), s(96)), radius=s(18), fill=(255, 255, 255, 34))
        img.alpha_composite(gloss.filter(ImageFilter.GaussianBlur(s(6))), (ox, oy))
        d = ImageDraw.Draw(img)
        # embossed hard-disk platter with its read arm: says "disk", not "phone"
        # (drawn on a layer: ImageDraw would replace the pixels' alpha instead of blending)
        emboss = Image.new("RGBA", img.size, (0, 0, 0, 0))
        e = ImageDraw.Draw(emboss)
        pcx, pcy, pr = cx, oy + s(72), s(40)
        e.ellipse((pcx - pr, pcy - pr, pcx + pr, pcy + pr), fill=(255, 255, 255, 28), outline=(255, 255, 255, 110), width=s(4))
        e.ellipse((pcx - s(22), pcy - s(22), pcx + s(22), pcy + s(22)), outline=(255, 255, 255, 70), width=s(2))
        e.ellipse((pcx - s(6), pcy - s(6), pcx + s(6), pcy + s(6)), fill=(255, 255, 255, 150))
        ax, ay = pcx + s(52), pcy + s(30)
        e.line((ax, ay, pcx + s(12), pcy - s(14)), fill=(255, 255, 255, 150), width=s(6), joint="curve")
        e.ellipse((ax - s(7), ay - s(7), ax + s(7), ay + s(7)), fill=(255, 255, 255, 150))
        img.alpha_composite(emboss)
        d = ImageDraw.Draw(img)
        # activity LED strip above the port
        lx, ly = cx, oy + bh - s(44)
        led = Image.new("RGBA", img.size, (0, 0, 0, 0))
        ImageDraw.Draw(led).rounded_rectangle((lx - s(30), ly - s(10), lx + s(30), ly + s(10)), radius=s(10), fill=(120, 255, 170, 120))
        img.alpha_composite(led.filter(ImageFilter.GaussianBlur(s(8))))
        d = ImageDraw.Draw(img)
        d.rounded_rectangle((lx - s(18), ly - s(3), lx + s(18), ly + s(3)), radius=s(3), fill=(150, 255, 190, 255))
        # USB plug in the port
        plug = gradient((plug_w, plug_h), (70, 74, 92), (30, 32, 46))
        img.alpha_composite(Image.composite(plug, Image.new("RGBA", plug.size, (0, 0, 0, 0)), rounded_mask(plug.size, s(8))), (cx - plug_w // 2, plug_y))
        d = ImageDraw.Draw(img)
        d.rounded_rectangle((cx - plug_w // 2 + s(8), plug_y + s(6), cx + plug_w // 2 - s(8), plug_y + s(10)), radius=s(2), fill=(140, 146, 170, 255))

    # label
    d = ImageDraw.Draw(img)
    lx0, ly0 = ox + s(26), oy + s(130)
    lx1, ly1 = ox + bw - s(26), ly0 + s(64)
    font = ImageFont.truetype(FONT_MONO, s(27))
    if ghost:
        d.rounded_rectangle((lx0, ly0, lx1, ly1), radius=s(10), outline=top + (200,), width=s(3))
        fill = top + (235,)
    else:
        d.rounded_rectangle((lx0, ly0, lx1, ly1), radius=s(10), fill=(246, 247, 252, 255))
        fill = (32, 30, 52, 255)
    tw = d.textlength(label, font=font)
    d.text(((lx0 + lx1 - tw) / 2, (ly0 + ly1) / 2), label, font=font, fill=fill, anchor="lm")
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
    place(canvas, drive("MOVIES02", (90, 220, 230), (90, 220, 230), ghost=True), (s(1094), s(400)), -15, shadow=False)
    place(canvas, drive("MOVIES00", hexrgb("#c07bd6"), hexrgb("#5d2386"), cable_bend=-1), (s(770), s(420)), 15)
    place(canvas, drive("MOVIES01", hexrgb("#1fb8ee"), hexrgb("#0b4f93")), (s(944), s(388)), 0)

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
