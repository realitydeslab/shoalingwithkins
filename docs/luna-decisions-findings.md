# Luna Decisions for touch detection — findings (2026-10-08)

Question: can OpenRouter's `openai/gpt-6-luna-decisions` tell, from a first-person camera frame,
whether the wearer's hand is touching a rock or a tree, fast enough to drive a HoloKit
interaction?

## The API

- Model `openai/gpt-6-luna-decisions` (endpoint `openai/gpt-6-luna-decisions-20261006`) is not in
  OpenRouter's public `/api/v1/models` list, but `/api/v1/models/openai/gpt-6-luna-decisions/endpoints`
  answers: modality `text+image->decisions`, provider OpenAI, $0.10 / M input tokens, no output cost.
- It speaks the System One contract: `POST https://openrouter.ai/api/v1/systemone` with
  `{model, state, questions}`; answers come back per question id (`noul` / `choice` / `score`).
- **Image format.** Only an OpenAI chat-style content part is read as an image:

  ```json
  "state": [{"type": "text", "text": "…context…"},
            {"type": "image_url", "image_url": {"url": "data:image/jpeg;base64,…"}}]
  ```

  Probe on one 384 px image (`blue` vs `rock` Nouls):

  | `state` shape | input tokens | read as image? |
  |---|---|---|
  | `[{"type":"image_url","image_url":{"url":…}}]` | 455 | yes (blue = 0.99) |
  | `[{"type":"input_image","image_url":…}]` | 21,783 | no |
  | `{"image": "data:…"}` | 21,777 | no |
  | bare data-URL string | 21,773 | no |
  | Anthropic `{"type":"image","source":…}` | 21,789 | no |

  The wrong shapes are not rejected — they are billed as ~21k tokens of base64 text and answered wrongly.

## Accuracy and latency

Test set: 14 generated first-person images (`tools/testset/`, prompts in `prompts.tsv`) — touching
rock ×3, touching tree ×3, hovering near rock ×2 (one with the hand's shadow on the stone), hovering
near / pointing at tree ×2, hand in water, hand on a bridge railing, no hand ×2 — plus 8 Wikimedia
Commons photos (one hand on bark, seven rocks/trunks without hands; not committed, licences vary).

| Setup | Accuracy | p50 latency | tokens |
|---|---|---|---|
| 3 questions (Choice + 2 Nouls), 512 px | 22/22 both designs | 380 ms | 775 |
| 256 px | 20/22 — hovering read as touching (p(rock) 0.64) | 317 ms | 631 |
| 384 px | Choice 22/22, Noul 21/22 | 363 ms | 691 |
| 768 px | 22/22 | 477 ms | 1015 |
| **App request** (1 Choice, 512 px, nearest-neighbour downsample like XRCpuImage), ×2 reps | **44/44** | **385 ms** (p90 520, max 962) | 428 |

![Test images with Luna's answers](testset-results.jpg)

*The 14 generated images with the app request's answers (first repetition); regenerate with
`python3 tools/results_sheet.py tools/out/<run>.json docs/testset-results.jpg tools/testset`.*

Highest kin probability on any non-touching image (app request): 0.30 (rock, hand in water),
0.22 (tree, hand hovering in front of a trunk), 0.19 (rock, hand above a small rock). Touches
score ≥ 0.99. Hence the matcher: enter at ≥ 0.6 on one answer, exit after two answers < 0.35.

Cost at 3 answers/s: 428 tokens × $0.1/M ≈ $0.000043 per call ≈ **$0.46 / hour**.

Reliability: 2 HTTP 504s seen in ~250 calls today (both succeeded on retry; one sweep discarded stderr, so 2 is a floor). The app skips failed
frames rather than retrying — the next frame is ~0.25 s away.

Measured from this Mac's network connection; phone-on-cellular latency at the creek is untested.

## Limits of this evidence

- Generated images are clean and well-lit; real headset frames have motion blur, partial hands,
  dappled light and the HoloKit's own view. The 44/44 is a smoke test, not a field accuracy.
- n is small (22 distinct images). A labelled set of real creek frames — e.g. the app's own
  "what Luna sees" frames — is the next evaluation step.
- Probabilities are near 0/1 here; calibration on ambiguous real contact (fingertips grazing
  moss on a rock, a hand on a log lying on stones) is unknown.

## Reproduce

```sh
OPENROUTER_API_KEY=… python3 tools/bench_touch.py tools/testset --nearest --reps 2
```
