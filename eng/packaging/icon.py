#!/usr/bin/env python3
"""Draws the Waddamburo icon, a mitsudomoe (the three-comma crest on taiko drum heads),
and writes waddamburo.png (AppImage) and waddamburo.ico (Windows) beside this script.
Needs numpy and Pillow; the outputs are committed, so packaging does not run this."""
from pathlib import Path

import numpy as np
from PIL import Image

N = 2048  # rendered large, then downsampled for antialiasing
RED, BLACK = (200, 24, 32), (18, 14, 16)

y, x = np.mgrid[0:N, 0:N].astype(np.float32)
x, y = (x + 0.5) / N * 2 - 1, (y + 0.5) / N * 2 - 1
radius = np.hypot(x, y)
image = np.zeros((N, N, 4), np.uint8)
image[radius <= 1.0] = (*BLACK, 255)
image[(radius >= 0.87) & (radius <= 0.93)] = (*RED, 255)

# Each comma is the union of discs along a path that spirals outward from its round head,
# shrinking to a point: head at radius 0.37 (size 0.19), tail to radius 0.72 over 135 degrees.
t = np.linspace(0, 1, 500)
for k in range(3):
    angle = np.pi / 2 + k * 2 * np.pi / 3 - np.radians(135) * t
    distance = 0.37 + (0.72 - 0.37) * t**0.8
    width = 0.19 * (1 - t) ** 1.6
    field = np.full((N, N), 9, np.float32)
    for cx, cy, w in zip(distance * np.cos(angle), distance * np.sin(angle), width):
        np.minimum(field, np.hypot(x - cx, y - cy) - w, out=field)
    image[field <= 0] = (*RED, 255)

icon = Image.fromarray(image, "RGBA").resize((256, 256), Image.LANCZOS)
here = Path(__file__).parent
icon.save(here / "waddamburo.png")
icon.save(here / "waddamburo.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
