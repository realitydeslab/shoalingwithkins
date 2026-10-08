"""Benchmark Luna Decisions on labelled touch images with the app's exact request.

Sends each image the way the HoloKit app does (one `choice` question over rock / tree / other /
nothing, the image as an `image_url` part in `state`) to OpenRouter's System One endpoint and
reports accuracy, the distribution per image, latency and cost.

Labels come from file names: touch_rock_*, touch_tree_*, other_*, and anything else
(hover_*, none_*) means "the hand touches no kin".

Usage:
    OPENROUTER_API_KEY=... python3 tools/bench_touch.py tools/testset [more dirs] \
        [--side 512] [--nearest] [--concurrency 4] [--reps 1] [--json tools/out/run.json]
"""

from __future__ import annotations

import argparse
import base64
import io
import json
import os
import statistics
import sys
import time
import urllib.error
import urllib.request
from concurrent.futures import ThreadPoolExecutor
from dataclasses import dataclass
from pathlib import Path

from PIL import Image

ENDPOINT = "https://openrouter.ai/api/v1/systemone"
MODEL = "openai/gpt-6-luna-decisions"

# Keep in sync with app/Assets/ShoalingWithKin/Core/DecisionsProtocol.cs and KinDefinition.Defaults().
CONTEXT = (
    "First-person view from a camera worn on the person's face while they walk along a creek. "
    "Any hand or arm reaching in from the bottom of the frame belongs to the wearer."
)
INSTRUCTIONS = (
    "What is the wearer's hand physically touching right now? Skin must be in contact; hovering near, "
    "pointing at, or casting a shadow on something is not touching."
)
CRITERIA = {
    "rock": "A rock, stone, or boulder",
    "tree": "A tree: its trunk, bark, branch, or roots (living or fallen)",
    "other": "Something else: water, ground, railing, clothing, another object",
    "nothing": "No hand visible, or the hand touches nothing",
}
KIN = ("rock", "tree")


@dataclass(frozen=True)
class Result:
    """One answered image."""

    name: str
    truth: str
    choice: str
    probabilities: dict[str, float]
    latency_s: float
    input_tokens: int
    cost: float

    @property
    def correct(self) -> bool:
        """Kin must match exactly; other and nothing both count as "no kin"."""
        if self.truth in KIN:
            return self.choice == self.truth
        return self.choice not in KIN


def label(stem: str) -> str:
    """Ground truth from the file name."""
    for kin in KIN:
        if stem.startswith(f"touch_{kin}"):
            return kin
    return "other" if stem.startswith("other") else "nothing"


def encode(path: Path, side: int, nearest: bool) -> str:
    """Downscale to `side` px wide (nearest = what the iPhone's XRCpuImage does) as base64 JPEG q75."""
    image = Image.open(path).convert("RGB")
    if nearest:
        image = image.resize((1920, 1440), Image.BICUBIC)
        image = image.resize((side, round(side * 1440 / 1920)), Image.NEAREST)
    else:
        image.thumbnail((side, side))
    buffer = io.BytesIO()
    image.save(buffer, "JPEG", quality=75)
    return base64.b64encode(buffer.getvalue()).decode()


def request_body(jpeg_b64: str) -> bytes:
    """The app's request."""
    return json.dumps({
        "model": MODEL,
        "state": [
            {"type": "text", "text": CONTEXT},
            {"type": "image_url", "image_url": {"url": f"data:image/jpeg;base64,{jpeg_b64}"}},
        ],
        "questions": {"touch": {"type": "choice", "instructions": INSTRUCTIONS, "criteria": CRITERIA}},
    }).encode()


def ask(path: Path, jpeg_b64: str, api_key: str) -> Result:
    """One request, retried with backoff on 429/5xx."""
    headers = {"Authorization": f"Bearer {api_key}", "Content-Type": "application/json"}
    for attempt in range(3):
        start = time.perf_counter()
        try:
            with urllib.request.urlopen(urllib.request.Request(ENDPOINT, request_body(jpeg_b64), headers), timeout=30) as r:
                body = json.load(r)
            answer = body["answers"]["touch"]
            return Result(path.stem, label(path.stem), answer["choice"], answer["probabilities"],
                          time.perf_counter() - start, body["usage"]["input_tokens"], body["usage"].get("cost", 0.0))
        except urllib.error.HTTPError as e:
            print(f"  {path.stem}: HTTP {e.code} (attempt {attempt + 1})", file=sys.stderr)
            if e.code < 500 and e.code != 429:
                raise
            time.sleep(0.5 * 2 ** attempt)
    raise RuntimeError(f"{path.stem}: gave up after 3 attempts")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("dirs", nargs="+", type=Path)
    parser.add_argument("--side", type=int, default=512)
    parser.add_argument("--nearest", action="store_true", help="nearest-neighbour downsample, as on the device")
    parser.add_argument("--concurrency", type=int, default=4)
    parser.add_argument("--reps", type=int, default=1)
    parser.add_argument("--json", type=Path, help="write all results here")
    args = parser.parse_args()

    api_key = os.environ.get("OPENROUTER_API_KEY")
    if not api_key:
        print("Set OPENROUTER_API_KEY.", file=sys.stderr)
        return 2
    files = sorted(p for d in args.dirs for p in d.iterdir() if p.suffix.lower() in (".jpg", ".jpeg", ".png"))
    images = {p: encode(p, args.side, args.nearest) for p in files}
    jobs = [p for _ in range(args.reps) for p in files]
    with ThreadPoolExecutor(args.concurrency) as pool:
        results = list(pool.map(lambda p: ask(p, images[p], api_key), jobs))

    print(f"{MODEL}  side={args.side}{' nearest' if args.nearest else ''}  n={len(results)}")
    print(f"{'image':18s} {'truth':7s} | {'choice':7s}   rock  tree other  none |    ms  tok")
    for r in results:
        p = r.probabilities
        print(f"{r.name:18s} {r.truth:7s} | {r.choice:7s} {'✓' if r.correct else '✗'} "
              f"{p.get('rock', 0):5.2f} {p.get('tree', 0):5.2f} {p.get('other', 0):5.2f} {p.get('nothing', 0):5.2f} | "
              f"{r.latency_s * 1000:5.0f} {r.input_tokens}")
    latencies = sorted(r.latency_s * 1000 for r in results)
    correct = sum(r.correct for r in results)
    print(f"accuracy {correct}/{len(results)}  latency p50 {statistics.median(latencies):.0f} ms  "
          f"p90 {latencies[int(len(latencies) * 0.9) - 1]:.0f} ms  max {latencies[-1]:.0f} ms  "
          f"cost ${sum(r.cost for r in results):.5f} (${statistics.mean(r.cost for r in results) * 3600 * 3:.2f}/h at 3 Hz)")
    if args.json:
        args.json.parent.mkdir(parents=True, exist_ok=True)
        args.json.write_text(json.dumps([r.__dict__ for r in results], indent=1))
    return 0 if correct == len(results) else 1


if __name__ == "__main__":
    sys.exit(main())
