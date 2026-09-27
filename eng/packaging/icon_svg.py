#!/usr/bin/env python3
"""Writes waddamburo.svg, the vector twin of icon.py's mitsudomoe (same geometry): each comma is
traced as the envelope of the discs icon.py sweeps along its spiral, plus its round head."""
import math
from pathlib import Path

RED, BLACK = "#c81820", "#120e10"
STEPS = 80


def comma(k: int) -> str:
    points = []
    for i in range(STEPS + 1):
        t = i / STEPS
        angle = math.pi / 2 + k * 2 * math.pi / 3 - math.radians(135) * t
        distance = 0.37 + (0.72 - 0.37) * t ** 0.8
        width = 0.19 * (1 - t) ** 1.6
        points.append((distance * math.cos(angle), distance * math.sin(angle), width))
    left, right = [], []
    for i, (x, y, w) in enumerate(points):
        # Normal of the path at this sample (central difference).
        ax, ay, _ = points[max(i - 1, 0)]
        bx, by, _ = points[min(i + 1, STEPS)]
        dx, dy = bx - ax, by - ay
        length = math.hypot(dx, dy)
        nx, ny = -dy / length, dx / length
        left.append((x + nx * w, y + ny * w))
        right.append((x - nx * w, y - ny * w))
    outline = left + right[::-1]
    path = "M" + " L".join(f"{x:.4f} {y:.4f}" for x, y in outline) + " Z"
    hx, hy, hw = points[0]
    return (f'<path d="{path}"/>'
            f'<circle cx="{hx:.4f}" cy="{hy:.4f}" r="{hw:.4f}"/>')


svg = (
    '<svg xmlns="http://www.w3.org/2000/svg" viewBox="-1 -1 2 2">'
    f'<circle r="1" fill="{BLACK}"/>'
    f'<circle r="0.9" fill="none" stroke="{RED}" stroke-width="0.06"/>'
    f'<g fill="{RED}">{"".join(comma(k) for k in range(3))}</g>'
    '</svg>\n'
)
(Path(__file__).parent / "waddamburo.svg").write_text(svg)
