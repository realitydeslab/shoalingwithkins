"""Draw a contact sheet of the test images annotated with Luna's answers from a bench_touch.py JSON.

Usage: python3 tools/results_sheet.py tools/out/run.json OUT.jpg DIR [DIR ...]
"""

from __future__ import annotations

import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

KIN_COLOURS = {"rock": (90, 170, 255), "tree": (90, 200, 70), "table": (230, 160, 60), "chair": (210, 90, 200),
               "other": (200, 200, 200), "nothing": (150, 150, 150)}
ORDER = ("touch_rock", "point_rock", "touch_tree", "point_table", "touch_table", "point_chair", "touch_chair",
         "hover", "wave", "other", "point_other", "none")


def sort_key(path: Path) -> tuple[int, str]:
    """Kin first, then hovers, other, no-hand."""
    return next((i for i, p in enumerate(ORDER) if path.stem.startswith(p)), len(ORDER)), path.stem


def main() -> None:
    results_path, out, dirs = Path(sys.argv[1]), Path(sys.argv[2]), [Path(d) for d in sys.argv[3:]]
    results: dict[str, dict] = {}
    for r in json.loads(results_path.read_text()):
        results.setdefault(r["name"], r)  # first repetition
    files = sorted((p for d in dirs for p in d.iterdir() if p.stem in results), key=sort_key)

    outcomes = list(next(iter(results.values()))["probabilities"])
    cols, tw, th, bar_h = 4, 400, 300, 50 + 17 * len(outcomes)
    rows = (len(files) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * tw, rows * (th + bar_h)), "white")
    draw = ImageDraw.Draw(sheet)
    try:
        font = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial.ttf", 17)
        bold = ImageFont.truetype("/System/Library/Fonts/Supplemental/Arial Bold.ttf", 18)
    except OSError:
        font = bold = ImageFont.load_default()

    for i, f in enumerate(files):
        r = results[f.stem]
        x, y = (i % cols) * tw, (i // cols) * (th + bar_h)
        im = Image.open(f).convert("RGB")
        im.thumbnail((tw - 8, th - 8))
        sheet.paste(im, (x + (tw - im.width) // 2, y + 4))
        ok = r.get("correct", r["choice"] == r["truth"] or (r["truth"] not in ("rock", "tree") and r["choice"] not in ("rock", "tree")))
        draw.text((x + 8, y + th + 2), f"{f.stem}", fill="black", font=bold)
        draw.text((x + 8, y + th + 24), f"truth: {r['truth']}   Luna: {r['choice']} {'correct' if ok else 'WRONG'}   {r['latency_s'] * 1000:.0f} ms",
                  fill=(0, 120, 0) if ok else (200, 0, 0), font=font)
        for j, k in enumerate(outcomes):
            p = r["probabilities"].get(k, 0.0)
            by = y + th + 48 + j * 17
            draw.text((x + 8, by - 2), k, fill="black", font=font)
            draw.rectangle([x + 80, by + 2, x + 80 + int(250 * p), by + 13], fill=KIN_COLOURS.get(k, (120, 120, 120)))
            draw.text((x + 340, by - 2), f"{p:.2f}", fill="black", font=font)
    sheet.save(out, quality=88)
    print(out, len(files), "images")


if __name__ == "__main__":
    main()
