#!/usr/bin/env python3
"""App icons (Android, iOS, macOS, Switch) from eng/packaging/waddamburo.svg: the logo on a graphite gradient.

The outputs are committed; rerun after changing the SVG. Needs cairosvg and Pillow
(python3 -m venv .venv && .venv/bin/pip install cairosvg pillow && .venv/bin/python eng/packaging/mobile_icons.py).

- Android: legacy square and round launcher PNGs, plus an adaptive icon (gradient background, logo foreground in the
  66dp safe zone, the logo as the themed-icon monochrome layer).
- iOS: one 1024 px opaque AppIcon (actool makes the other sizes).
- macOS: the tile rounded inside a 1024 px canvas (Apple's grid: 824 px tile, 185 px corners), which
  eng/package.sh turns into the .icns.
- Switch: the NRO's 256 px baseline JPEG; the forwarder NSP copies it into every language's icon_*.dat.
"""
import json
from io import BytesIO
from pathlib import Path

import cairosvg
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[2]
SVG = Path(__file__).with_name("waddamburo.svg")
ANDROID = ROOT / "src/Waddamburo.Android/Resources"
IOS = ROOT / "src/Waddamburo.iOS/Assets.xcassets"
SWITCH = ROOT / "platforms/switch/icon.jpg"
MACOS = Path(__file__).with_name("macos-icon.png")
TOP_LEFT, BOTTOM_RIGHT = (0x55, 0x50, 0x59), (0x29, 0x26, 0x2C)
DENSITIES = {"mdpi": 1, "hdpi": 1.5, "xhdpi": 2, "xxhdpi": 3, "xxxhdpi": 4}


def gradient(size: int) -> Image.Image:
    # A diagonal blend: a 2x2 image stretched bilinearly gives exactly (x + y) / 2.
    corners = Image.new("RGB", (2, 2))
    middle = tuple((a + b) // 2 for a, b in zip(TOP_LEFT, BOTTOM_RIGHT))
    corners.putdata([TOP_LEFT, middle, middle, BOTTOM_RIGHT])
    return corners.resize((size, size), Image.Resampling.BILINEAR, box=(0.5, 0.5, 1.5, 1.5)).convert("RGBA")


def logo(size: int, scale: float) -> Image.Image:
    side = round(size * scale)
    art = Image.open(BytesIO(cairosvg.svg2png(url=str(SVG), output_width=side, output_height=side))).convert("RGBA")
    canvas = Image.new("RGBA", (size, size))
    canvas.alpha_composite(art, ((size - side) // 2, (size - side) // 2))
    return canvas


def tile(size: int) -> Image.Image:
    icon = gradient(size)
    icon.alpha_composite(logo(size, 0.78))
    return icon


def round_tile(size: int) -> Image.Image:
    icon = tile(size * 4)  # drawn large so the circle's edge is smooth
    mask = Image.new("L", icon.size)
    ImageDraw.Draw(mask).ellipse((0, 0, icon.width - 1, icon.height - 1), fill=255)
    icon.putalpha(mask)
    return icon.resize((size, size), Image.Resampling.LANCZOS)


ADAPTIVE = """<?xml version="1.0" encoding="utf-8"?>
<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">
    <background android:drawable="@mipmap/ic_launcher_background" />
    <foreground android:drawable="@mipmap/ic_launcher_foreground" />
    <monochrome android:drawable="@mipmap/ic_launcher_foreground" />
</adaptive-icon>
"""

for name, factor in DENSITIES.items():
    folder = ANDROID / f"mipmap-{name}"
    folder.mkdir(parents=True, exist_ok=True)
    tile(round(48 * factor)).save(folder / "ic_launcher.png")
    round_tile(round(48 * factor)).save(folder / "ic_launcher_round.png")
    layer = round(108 * factor)
    gradient(layer).save(folder / "ic_launcher_background.png")
    logo(layer, 0.78 * 72 / 108).save(folder / "ic_launcher_foreground.png")  # as on the tiles: 78% of the 72dp shown
anydpi = ANDROID / "mipmap-anydpi-v26"
anydpi.mkdir(parents=True, exist_ok=True)
for name in ("ic_launcher.xml", "ic_launcher_round.xml"):
    (anydpi / name).write_text(ADAPTIVE)

appicon = IOS / "AppIcon.appiconset"
appicon.mkdir(parents=True, exist_ok=True)
tile(1024).convert("RGB").save(appicon / "AppIcon-1024.png")  # App Store icons must be opaque
(IOS / "Contents.json").write_text(json.dumps({"info": {"author": "xcode", "version": 1}}, indent=2) + "\n")
(appicon / "Contents.json").write_text(json.dumps({
    "images": [{"filename": "AppIcon-1024.png", "idiom": "universal", "platform": "ios", "size": "1024x1024"}],
    "info": {"author": "xcode", "version": 1},
}, indent=2) + "\n")

mac = tile(824 * 4)  # drawn large so the corners are smooth
mask = Image.new("L", mac.size)
ImageDraw.Draw(mask).rounded_rectangle((0, 0, mac.width - 1, mac.height - 1), radius=185 * 4, fill=255)
mac.putalpha(mask)
canvas = Image.new("RGBA", (1024, 1024))
canvas.alpha_composite(mac.resize((824, 824), Image.Resampling.LANCZOS), (100, 100))
canvas.save(MACOS)

tile(256).convert("RGB").save(SWITCH, "JPEG", quality=92, subsampling=2, progressive=False)
print("written: Android mipmaps, iOS AppIcon, macOS icon, Switch icon.jpg")
