#!/usr/bin/env python3
"""Draw the IMDb and Rotten Tomatoes marks into design/scores.json (run by hand when a mark changes, then `make tokens`).

Simplified drawings, not the official artwork. Paths are absolute M, L, C and Z only, so every client can draw them."""
import json
import math
from pathlib import Path

OUT = Path(__file__).resolve().parents[2] / "design" / "scores.json"
K = 0.5523  # control-point distance for a quarter circle drawn as a cubic
YELLOW, BLACK, RED, LEAF, WHITE, SPLAT = "#F5C518", "#000000", "#FA320A", "#00912D", "#FFFFFF", "#0AC855"


def n(x):
    return f"{round(x, 2):g}"


def ellipse(cx, cy, rx, ry):
    kx, ky = K * rx, K * ry
    return (f"M{n(cx)} {n(cy - ry)} C{n(cx + kx)} {n(cy - ry)} {n(cx + rx)} {n(cy - ky)} {n(cx + rx)} {n(cy)} "
            f"C{n(cx + rx)} {n(cy + ky)} {n(cx + kx)} {n(cy + ry)} {n(cx)} {n(cy + ry)} "
            f"C{n(cx - kx)} {n(cy + ry)} {n(cx - rx)} {n(cy + ky)} {n(cx - rx)} {n(cy)} "
            f"C{n(cx - rx)} {n(cy - ky)} {n(cx - kx)} {n(cy - ry)} {n(cx)} {n(cy - ry)} Z")


def rounded(x, y, w, h, r):
    k = K * r
    return (f"M{n(x + r)} {n(y)} L{n(x + w - r)} {n(y)} C{n(x + w - r + k)} {n(y)} {n(x + w)} {n(y + r - k)} {n(x + w)} {n(y + r)} "
            f"L{n(x + w)} {n(y + h - r)} C{n(x + w)} {n(y + h - r + k)} {n(x + w - r + k)} {n(y + h)} {n(x + w - r)} {n(y + h)} "
            f"L{n(x + r)} {n(y + h)} C{n(x + r - k)} {n(y + h)} {n(x)} {n(y + h - r + k)} {n(x)} {n(y + h - r)} "
            f"L{n(x)} {n(y + r)} C{n(x)} {n(y + r - k)} {n(x + r - k)} {n(y)} {n(x + r)} {n(y)} Z")


def poly(points):
    return "M" + " L".join(f"{n(x)} {n(y)}" for x, y in points) + " Z"


def rect(x, y, w, h):
    return poly([(x, y), (x + w, y), (x + w, y + h), (x, y + h)])


def imdb():
    return [
        {"fill": YELLOW, "d": rounded(0, 0, 64, 32, 5)},
        {"fill": BLACK, "d": rect(8, 7, 4, 18)},
        {"fill": BLACK, "d": poly([(14, 7), (19, 7), (22, 17), (25, 7), (30, 7), (30, 25), (26.5, 25), (26.5, 13.5),
                                   (23.5, 25), (20.5, 25), (17.5, 13.5), (17.5, 25), (14, 25)])},
        {"fill": BLACK, "d": "M32 7 L38 7 C42.5 7 44 9.5 44 16 C44 22.5 42.5 25 38 25 L32 25 Z"},
        {"fill": YELLOW, "d": "M35.5 10.5 L37.5 10.5 C39.8 10.5 40.5 12 40.5 16 C40.5 20 39.8 21.5 37.5 21.5 L35.5 21.5 Z"},
        {"fill": BLACK, "d": rect(46, 7, 3.5, 18)},
        {"fill": BLACK, "d": "M49.5 13 C51 12.2 52 12 53 12 C55.6 12 56.5 14 56.5 18.5 C56.5 23 55.6 25 53 25 C52 25 51 24.6 49.5 23.6 Z"},
        {"fill": YELLOW, "d": "M49.5 15.6 C50.4 15.2 51 15 51.7 15 C52.8 15 53.2 16 53.2 18.5 C53.2 21 52.8 22 51.7 22 C51 22 50.4 21.8 49.5 21.3 Z"},
    ]


def tomato(s):
    """A tomato of scale s centred in the 32-unit square: the fruit and its five-pointed leaves."""
    cy = 16 - 0.4 * s
    leaves = [(0, -9.6), (-4.6, -13.2), (-2.6, -9.8), (-6.4, -9.4), (-2.2, -8.2), (0, -6.4), (2.2, -8.2), (6.4, -9.4),
              (2.6, -9.8), (4.6, -13.2)]
    return [{"fill": RED, "d": ellipse(16, cy + 1.5 * s, 12 * s, 11 * s)},
            {"fill": LEAF, "d": poly([(16 + x * s, cy + (y + 1.5) * s) for x, y in leaves])}]


def certified():
    seal = []
    for i in range(32):
        a = math.pi * 2 * i / 32 - math.pi / 2
        r = 15.6 if i % 2 == 0 else 14
        seal.append((16 + r * math.cos(a), 16 + r * math.sin(a)))
    return [{"fill": RED, "d": poly(seal)}, {"fill": WHITE, "d": ellipse(16, 16, 12, 12)}, *tomato(0.68)]


def rotten():
    points = []
    for i in range(56):
        a = math.pi * 2 * i / 56
        r = 10.2 + 2.6 * math.sin(7 * a + 0.6) + 1.3 * math.sin(3 * a + 1.9) + (2.8 if i % 8 == 3 else 0)
        points.append((16 + r * math.cos(a), 16.5 + r * math.sin(a) * 0.92))
    return [{"fill": SPLAT, "d": poly(points)}]


def main():
    data = {
        "about": "The IMDb and Rotten Tomatoes marks beside a title's scores (docs/design.md › Scores). Simplified drawings, "
                 "not the official artwork; brand colours appear only here. Drawn by scripts/design/draw_scores.py; "
                 "`make tokens` writes them out for every client.",
        "marks": {
            "imdb": {"use": "IMDb, beside the score out of 10", "width": 64, "height": 32, "paths": imdb()},
            "certifiedFresh": {"use": "Rotten Tomatoes Certified Fresh", "width": 32, "height": 32, "paths": certified()},
            "fresh": {"use": "Rotten Tomatoes Fresh, 60% and up", "width": 32, "height": 32, "paths": tomato(1)},
            "rotten": {"use": "Rotten Tomatoes Rotten, under 60%", "width": 32, "height": 32, "paths": rotten()},
        },
    }
    OUT.write_text(json.dumps(data, indent=1, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
