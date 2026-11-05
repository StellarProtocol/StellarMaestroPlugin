"""Regenerate the four flat, transparent Maestro hub icons with Pillow.

Run from any directory (Windows development machine):
    C:\\ProgramData\\anaconda3\\python.exe tools/make-hub-icons.py

The launcher reference, Resources/maestro-icon.png, is 128 x 128, pure white,
with visible bounds (22, 9, 106, 119), 6-7 px stems, filled note heads and
gently rounded corners. These silhouettes use comparable optical padding,
7-9 px structural strokes and solid details, without tiles or effects.
Geometry uses 128 px design units. Alpha is drawn at 4x and reduced with
LANCZOS; RGB stays pure white, including partially transparent edge pixels.
Only the four named Resources/icon-*.png files are written.
"""

import math
from pathlib import Path

from PIL import Image, ImageDraw


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


def main():
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


if __name__ == "__main__":
    main()
