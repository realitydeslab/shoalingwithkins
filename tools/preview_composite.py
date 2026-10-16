"""Composite the Unity-rendered shoal (app/Builds/Preview/fish_*.png, from ShoalingPreview.Render) over
a test photo standing in for the camera feed, and draw the Mono HUD with Luna's real answer for that photo
(from a bench_touch.py JSON). Layout follows KinHud.OnGUI.

Usage: python3 tools/preview_composite.py tools/out/run.json OUT_DIR
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent
SHOTS = [  # (fish layer, photo, caption)
    ("roaming", "none_creek", "Roaming: the shoal circles you; Luna sees no touch"),
    ("rock", "touch_rock_1", "Hand on a stone: matched rock, the fish swirl in and slip into the stone"),
    ("tree", "touch_tree_1", "Hand on bark: matched tree, the fish spiral up the trunk"),
]
COLOURS = {"rock": (140, 217, 255), "tree": (166, 255, 115), "other": (217, 217, 217), "nothing": (128, 128, 128)}
OUTCOMES = ("rock", "tree", "other", "nothing")


def font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    name = "Arial Bold.ttf" if bold else "Arial.ttf"
    try:
        return ImageFont.truetype(f"/System/Library/Fonts/Supplemental/{name}", size)
    except OSError:
        return ImageFont.load_default()


def cover(photo: Image.Image, size: tuple[int, int]) -> Image.Image:
    """Scale and centre-crop to fill `size`, like the AR camera background."""
    w, h = size
    scale = max(w / photo.width, h / photo.height)
    im = photo.resize((round(photo.width * scale), round(photo.height * scale)), Image.LANCZOS)
    left, top = (im.width - w) // 2, (im.height - h) // 2
    return im.crop((left, top, left + w, top + h))


def hud(canvas: Image.Image, result: dict, frame: Image.Image) -> None:
    """The Mono HUD: panel, title, bars, stats, 'what Luna sees' thumbnail, Pause button."""
    s = canvas.height / 1000
    overlay = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(overlay)
    x, y, w, row = 30 * s, 190 * s, 640 * s, 40 * s
    d.rectangle([x - 12 * s, y - 10 * s, x + w + 12 * s, y - 10 * s + row * 8.6], fill=(0, 0, 0, 140))
    choice = result["choice"]
    matched = choice if choice in ("rock", "tree") else None
    d.text((x, y), f"Touching {matched}" if matched else "Touching: —", font=font(int(40 * s), True),
           fill=COLOURS.get(matched, (255, 255, 255)) if matched else (255, 255, 255))
    y += row * 1.4
    for k in OUTCOMES:
        p = result["probabilities"].get(k, 0.0)
        d.text((x, y + 4 * s), k, font=font(int(26 * s)), fill=COLOURS[k])
        bar_w = (w - 290 * s) * p
        d.rectangle([x + 170 * s, y + row * 0.2, x + 170 * s + max(bar_w, 1), y + row * 0.8], fill=COLOURS[k])
        d.text((x + w - 110 * s, y + 4 * s), f"{p * 100:.0f}%", font=font(int(26 * s)), fill=COLOURS[k])
        y += row
    small = font(int(20 * s))
    d.text((x, y + 4 * s), f"openai/gpt-6-luna-decisions  ·  {result['latency_s'] * 1000:.0f} ms  ·  3.1 Hz", font=small, fill="white")
    d.text((x, y + row * 0.8 + 4 * s), "1 in flight · 0 errors · hand seen", font=small, fill="white")
    # "what Luna sees"
    fw = int(360 * s)
    fh = int(fw * frame.height / frame.width)
    thumb = frame.resize((fw, fh), Image.LANCZOS)
    fx, fy = int(canvas.width - fw - 30 * s), int(canvas.height - fh - 170 * s)
    canvas.paste(thumb, (fx, fy))
    d.text((fx, canvas.height - 165 * s), "what Luna sees", font=small, fill="white")
    # Pause button
    bx, by = x, canvas.height - 170 * s
    d.rounded_rectangle([bx, by, bx + 260 * s, by + 80 * s], radius=8 * s, fill=(60, 60, 60, 220))
    d.text((bx + 40 * s, by + 24 * s), "Pause Luna", font=font(int(26 * s)), fill="white")
    canvas.alpha_composite(overlay)


def main() -> None:
    results = {}
    for r in json.loads(Path(sys.argv[1]).read_text()):
        results.setdefault(r["name"], r)
    out = Path(sys.argv[2])
    out.mkdir(parents=True, exist_ok=True)
    for layer, photo_name, caption in SHOTS:
        fish = Image.open(ROOT / f"app/Builds/Preview/fish_{layer}.png").convert("RGBA")
        photo = Image.open(ROOT / f"tools/testset/{photo_name}.jpg").convert("RGB")
        canvas = cover(photo, fish.size).convert("RGBA")
        canvas.alpha_composite(fish)
        hud(canvas, results[photo_name], cover(photo, (512, 384)))
        path = out / f"preview_{layer}.jpg"
        canvas.convert("RGB").save(path, quality=88)
        print(path, "|", caption)


if __name__ == "__main__":
    main()
