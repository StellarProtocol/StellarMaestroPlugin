"""Draws the Maestro hub-card icons (Resources/icon-*.png).

64x64 rounded-square tiles in the Maestro gradient (#8a4fd8 -> #c8508f, diagonal) with a soft top-left highlight and a
simple white symbol, matching maestro-icon.png. Drawn at 4x and downscaled with Lanczos for clean edges. Shown at
32 px on the main window's hub cards (art does not follow the theme, Stellar-Design-Language.md section 7).

Run (Python comes from conda on the dev machine):  python tools/make-hub-icons.py
"""
import math
import os

from PIL import Image, ImageChops, ImageDraw, ImageFilter

OUT = 64
SS = 4
S = OUT * SS
RADIUS = 13 * SS
C0 = (0x8A, 0x4F, 0xD8)
C1 = (0xC8, 0x50, 0x8F)
WHITE = (255, 255, 255, 255)
HERE = os.path.dirname(os.path.abspath(__file__))
RES = os.path.join(HERE, "..", "Resources")


def tile():
    grad = Image.new("RGBA", (S, S))
    px = grad.load()
    for y in range(S):
        for x in range(S):
            t = (x + y) / (2 * (S - 1))
            px[x, y] = tuple(int(C0[i] + (C1[i] - C0[i]) * t) for i in range(3)) + (255,)
    # Soft top-left highlight.
    glow = Image.new("L", (S, S), 0)
    ImageDraw.Draw(glow).ellipse((-S * 0.35, -S * 0.45, S * 0.75, S * 0.45), fill=70)
    glow = glow.filter(ImageFilter.GaussianBlur(S * 0.08))
    white = Image.new("RGBA", (S, S), (255, 255, 255, 255))
    grad = Image.composite(white, grad, glow)
    mask = Image.new("L", (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, S - 1, S - 1), RADIUS, fill=255)
    grad.putalpha(mask)
    return grad


def u(v):  # 64-px design units -> supersampled pixels
    return v * SS


def draw_library(d):
    # A list: three bullets + lines.
    for i, y in enumerate((20, 32, 44)):
        d.ellipse((u(15), u(y - 3.5), u(22), u(y + 3.5)), fill=WHITE)
        d.rounded_rectangle((u(27), u(y - 3), u(50), u(y + 3)), u(3), fill=WHITE)


def draw_link(d):
    # Two interlocked rings.
    w = u(5.5)
    d.ellipse((u(10), u(18), u(38), u(46)), outline=WHITE, width=int(w))
    d.ellipse((u(26), u(18), u(54), u(46)), outline=WHITE, width=int(w))


def draw_preview(d):
    # Headphones: a headband arc + two ear cups.
    w = int(u(5.5))
    d.arc((u(13), u(13), u(51), u(51)), 180, 360, fill=WHITE, width=w)
    d.line((u(15.75), u(32), u(15.75), u(40)), fill=WHITE, width=w)
    d.line((u(48.25), u(32), u(48.25), u(40)), fill=WHITE, width=w)
    d.rounded_rectangle((u(12), u(34), u(23), u(51)), u(4), fill=WHITE)
    d.rounded_rectangle((u(41), u(34), u(52), u(51)), u(4), fill=WHITE)


def draw_settings(d, img):
    # Gear: 8 teeth around a ring with a hole.
    cx = cy = u(32)
    teeth = 8
    outer, inner = u(20), u(14.5)
    pts = []
    for k in range(teeth * 4):
        a = (k / (teeth * 4)) * 2 * math.pi
        r = outer if (k % 4) in (1, 2) else inner
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    d.polygon(pts, fill=WHITE)
    d.ellipse((cx - u(15.5), cy - u(15.5), cx + u(15.5), cy + u(15.5)), fill=WHITE)
    # Punch the centre hole back to the tile.
    hole = Image.new("L", img.size, 0)
    ImageDraw.Draw(hole).ellipse((cx - u(6.5), cy - u(6.5), cx + u(6.5), cy + u(6.5)), fill=255)
    return hole


def make(name, painter):
    base = tile()
    glyph = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(glyph)
    hole = painter(d, glyph) if painter is draw_settings else painter(d)
    if hole is not None:
        a = glyph.getchannel("A")
        glyph.putalpha(ImageChops.subtract(a, hole))
    out = Image.alpha_composite(base, glyph).resize((OUT, OUT), Image.LANCZOS)
    path = os.path.join(RES, name)
    out.save(path)
    print("wrote", os.path.normpath(path))


if __name__ == "__main__":
    make("icon-library.png", draw_library)
    make("icon-link.png", draw_link)
    make("icon-preview.png", draw_preview)
    make("icon-settings.png", draw_settings)
