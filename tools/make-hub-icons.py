"""Regenerate the flat, transparent Maestro hub and play-mode icons with Pillow.

Run from any directory (Windows development machine):
    C:\\ProgramData\\anaconda3\\python.exe tools/make-hub-icons.py

The launcher reference, Resources/maestro-icon.png, is 128 x 128, pure white,
with visible bounds (22, 9, 106, 119), 6-7 px stems, filled note heads and
gently rounded corners. These silhouettes use comparable optical padding,
7-9 px structural strokes and solid details, without tiles or effects.
Geometry uses 128 px design units. Alpha is drawn at 4x and reduced with
LANCZOS; hub RGB stays pure white, including partially transparent edge pixels.
Play-mode icons use slightly heavier 10 px strokes for their 18 px display size,
with green #5fd38d and muted grey #9b8aa5 RGB, including transparent pixels.
The four original hub icons and seven named play-mode PNGs are written.
Optionally pass --preview PATH to write an uncommitted size/colour proof sheet.
"""

import argparse
import math
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont


RESOURCES = Path(__file__).resolve().parent.parent / "Resources"
SUPERSAMPLE = 4
DESIGN_SIZE = 128


def box(draw, bounds, radius=0, fill=255):
    draw.rounded_rectangle(
        tuple(round(v * SUPERSAMPLE) for v in bounds),
        radius=round(radius * SUPERSAMPLE),
        fill=fill,
    )


def ellipse(draw, bounds, fill=255):
    draw.ellipse(tuple(round(v * SUPERSAMPLE) for v in bounds), fill=fill)


def stroke(draw, points, width):
    """A continuous stroke with round joins and caps, in design units."""
    draw.line(
        [(round(x * SUPERSAMPLE), round(y * SUPERSAMPLE)) for x, y in points],
        fill=255,
        width=round(width * SUPERSAMPLE),
        joint="curve",
    )
    r = width / 2
    for x, y in (points[0], points[-1]):
        ellipse(draw, (x - r, y - r, x + r, y + r))


def cubic(start, control1, control2, end):
    """Sample a cubic curve densely enough for the 4x alpha mask."""
    points = []
    for step in range(1, 49):
        t = step / 48
        s = 1 - t
        points.append(tuple(
            s**3 * start[i] + 3 * s*s*t * control1[i]
            + 3 * s*t*t * control2[i] + t**3 * end[i]
            for i in (0, 1)
        ))
    return points


def library(draw):
    # Three list rows and a filled eighth note; no enclosing card.
    for y, right in ((25, 63), (54, 58), (83, 49)):
        box(draw, (14, y, right, y + 8), radius=2)
    box(draw, (94, 17, 101, 95), radius=2)
    flag = [(98, 17), (114, 29), (114, 48), (98, 36)]
    draw.polygon([(x * SUPERSAMPLE, y * SUPERSAMPLE) for x, y in flag], fill=255)
    ellipse(draw, (73, 81, 101, 110))


def link(draw):
    # Two open capsule loops interlock diagonally. The openings keep the
    # crossings legible at 32 px instead of merging into a white knot.
    upper = [(56, 70)]
    upper += cubic(upper[-1], (64, 80), (79, 81), (88, 72))
    upper.append((105, 55))
    upper += cubic(upper[-1], (116, 44), (116, 27), (105, 17))
    upper += cubic(upper[-1], (94, 6), (77, 6), (66, 17))
    upper.append((55, 28))
    lower = [(72, 58)]
    lower += cubic(lower[-1], (64, 48), (49, 47), (40, 56))
    lower.append((23, 73))
    lower += cubic(lower[-1], (12, 84), (12, 101), (23, 111))
    lower += cubic(lower[-1], (34, 122), (51, 122), (62, 111))
    lower.append((73, 100))
    for points in (upper, lower):
        points = [(64 + (x - 64) * .88, 64 + (y - 64) * .88) for x, y in points]
        stroke(draw, points, width=8)


def preview(draw):
    # An 8 px headband with solid ear cups, like the reference's thin stems
    # and heavier note heads. The interior is genuinely transparent.
    band = [(64 + 46 * math.cos(a), 62 + 46 * math.sin(a))
            for a in (math.pi + math.pi * i / 128 for i in range(129))]
    stroke(draw, band, width=8)
    box(draw, (14, 61, 22, 88), radius=3)
    box(draw, (106, 61, 114, 88), radius=3)
    box(draw, (17, 65, 37, 111), radius=5)
    box(draw, (91, 65, 111, 111), radius=5)


def settings(draw):
    # Eight broad teeth and a circular cutout; subtly soften tooth corners
    # with short quadratic curves rather than any blur or shadow.
    vertices = []
    for tooth in range(8):
        for degrees, radius in ((-22.5, 38), (-11, 50), (11, 50), (22.5, 38)):
            a = math.radians(tooth * 45 + degrees)
            point = (64 + radius * math.cos(a), 64 + radius * math.sin(a))
            if not vertices or math.dist(point, vertices[-1]) > .01:
                vertices.append(point)
    if math.dist(vertices[0], vertices[-1]) < .01:
        vertices.pop()
    contour = []
    for i, vertex in enumerate(vertices):
        before, after = vertices[i - 1], vertices[(i + 1) % len(vertices)]

        def inset(other):
            fraction = min(2 / math.dist(vertex, other), .25)
            return tuple(vertex[j] + (other[j] - vertex[j]) * fraction for j in (0, 1))

        start, end = inset(before), inset(after)
        for step in range(9):
            t = step / 8
            contour.append(tuple((1-t)**2 * start[j] + 2*(1-t)*t * vertex[j]
                                 + t*t * end[j] for j in (0, 1)))
    draw.polygon([(round(x * SUPERSAMPLE), round(y * SUPERSAMPLE)) for x, y in contour], fill=255)
    ellipse(draw, (34, 34, 94, 94), fill=0)


def autonext(draw):
    # One forward arrow and a separate stop bar, with a clear gap at 18 px.
    stroke(draw, [(20, 64), (81, 64)], width=10)
    stroke(draw, [(62, 41), (85, 64), (62, 87)], width=10)
    box(draw, (102, 30, 112, 98), radius=3)


def loop(draw):
    # Opposing horizontal arrows with rounded returns; the centre stays open.
    top = [(22, 57), (22, 49)]
    top += cubic(top[-1], (22, 38), (28, 32), (40, 32))
    top.append((103, 32))
    stroke(draw, top, width=10)
    stroke(draw, [(86, 17), (103, 32), (86, 47)], width=10)
    bottom = [(106, 71), (106, 79)]
    bottom += cubic(bottom[-1], (106, 90), (100, 96), (88, 96))
    bottom.append((25, 96))
    stroke(draw, bottom, width=10)
    stroke(draw, [(42, 81), (25, 96), (42, 111)], width=10)


def loop_one(draw):
    loop(draw)
    # Hand-drawn bold numeral avoids font dependencies and survives downsampling.
    stroke(draw, [(57, 54), (66, 47), (66, 81)], width=10)
    stroke(draw, [(57, 81), (75, 81)], width=9)


def shuffle(draw):
    # Two smooth crossing routes with separated arrowheads on the right.
    for start_y, end_y in ((32, 96), (96, 32)):
        route = [(20, start_y), (34, start_y)]
        route += cubic(route[-1], (55, start_y), (73, end_y), (94, end_y))
        route.append((106, end_y))
        stroke(draw, route, width=10)
        stroke(draw, [(90, end_y - 15), (106, end_y), (90, end_y + 15)], width=10)


PLAYMODE_ICONS = (
    ("autonext", autonext, ("on", "off")),
    ("loop", loop, ("on", "off")),
    ("loop-one", loop_one, ("on",)),
    ("shuffle", shuffle, ("on", "off")),
)
PLAYMODE_COLOURS = {"on": (95, 211, 141), "off": (155, 138, 165)}


def playmode_icon(painter, colour):
    alpha = Image.new("L", (DESIGN_SIZE * SUPERSAMPLE,) * 2, 0)
    painter(ImageDraw.Draw(alpha))
    alpha = alpha.resize((DESIGN_SIZE,) * 2, Image.Resampling.LANCZOS)
    icon = Image.new("RGBA", alpha.size, (*colour, 0))
    icon.putalpha(alpha)
    return icon


def preview_sheet(path):
    """Show every PNG at 128 px, native 18 px, and an enlarged chip proof."""
    background = (34, 28, 38, 255)
    sheet = Image.new("RGBA", (1152, 576), background)
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 15)
        small = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 13)
    except OSError:
        font = small = ImageFont.load_default()
    draw.text((24, 14), "Maestro play modes | 128 px originals / 18 px chips / 4x chip detail",
              font=font, fill="#e7dfea")
    index = 0
    for name, _, variants in PLAYMODE_ICONS:
        for variant in variants:
            x = 16 + index * 160
            draw.text((x, 48), f"{name} / {variant}", font=small, fill="#e7dfea")
            with Image.open(RESOURCES / f"icon-{name}-{variant}.png") as icon:
                sheet.alpha_composite(icon, (x + 8, 76))
                chip = Image.new("RGBA", (36, 28), background)
                wash = Image.new("RGBA", chip.size)
                alpha = round(255 * (.25 if variant == "on" else .15))
                ImageDraw.Draw(wash).rounded_rectangle(
                    (0, 0, 35, 27), radius=7,
                    fill=(*PLAYMODE_COLOURS[variant], alpha),
                )
                chip = Image.alpha_composite(chip, wash)
                chip.alpha_composite(icon.resize((18, 18), Image.Resampling.LANCZOS), (9, 5))
                sheet.alpha_composite(chip, (x + 54, 236))
                sheet.alpha_composite(chip.resize((144, 112), Image.Resampling.NEAREST), (x, 300))
            index += 1
    draw.text((24, 438), "Existing family references (64 px)", font=font, fill="#e7dfea")
    for index, name in enumerate(("maestro-icon", "icon-library", "icon-link", "icon-preview", "icon-settings")):
        x = 24 + index * 160
        with Image.open(RESOURCES / f"{name}.png") as icon:
            sheet.alpha_composite(icon.convert("RGBA").resize((64, 64), Image.Resampling.LANCZOS), (x, 470))
        draw.text((x, 544), name, font=small, fill="#b7a9c0")
    path.parent.mkdir(parents=True, exist_ok=True)
    sheet.convert("RGB").save(path)
    print(f"Wrote preview {path}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--preview", type=Path, help="optional PNG proof sheet path")
    args = parser.parse_args()
    with Image.open(RESOURCES / "maestro-icon.png") as reference:
        canvas = reference.size
    if canvas != (DESIGN_SIZE, DESIGN_SIZE):
        raise ValueError(f"Expected the 128 x 128 launcher reference, got {canvas}")
    for name, painter in (("library", library), ("link", link),
                          ("preview", preview), ("settings", settings)):
        alpha = Image.new("L", tuple(v * SUPERSAMPLE for v in canvas), 0)
        painter(ImageDraw.Draw(alpha))
        alpha = alpha.resize(canvas, Image.Resampling.LANCZOS)
        icon = Image.new("RGBA", canvas, (255, 255, 255, 0))
        icon.putalpha(alpha)
        path = RESOURCES / f"icon-{name}.png"
        icon.save(path, optimize=False)
        print(f"Wrote {path}")
    for name, painter, variants in PLAYMODE_ICONS:
        for variant in variants:
            path = RESOURCES / f"icon-{name}-{variant}.png"
            playmode_icon(painter, PLAYMODE_COLOURS[variant]).save(path, optimize=False)
            print(f"Wrote {path}")
    if args.preview:
        preview_sheet(args.preview)


if __name__ == "__main__":
    main()
