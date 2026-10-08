# Task plan — Shoaling With Kin (HoloKit × Luna Decisions)

Goal: a HoloKit app that shows, in near real time, the probability that the wearer's hand is
touching a rock or a tree (via OpenRouter `openai/gpt-6-luna-decisions`), and on a match plays
music and sends the fish shoal into that kin.

## Decisions (with evidence)

| Decision | Why |
|---|---|
| Endpoint `POST https://openrouter.ai/api/v1/systemone` | System One contract (state + typed questions); 200 with this model, ~0.23 s text-only |
| Image as `[{"type":"image_url","image_url":{"url":"data:image/jpeg;base64,…"}}]` | Only shape read as an image (455 tok); every other shape is read as ~21k tok of base64 text |
| One `choice` question over kin + `other` + `nothing` | 22/22 on the test set incl. hover negatives; gives a full distribution to display |
| 512 px JPEG, nearest-neighbour downsample | 256 px misreads hovering as touching; 768 px is +100 ms with no gain; NN = what XRCpuImage does, still 22/22 |
| Unity 6000.3.24f1 + HoloKit SDK pinned `git+file …#7ef2463` | Matches `holokit-simple-app`; the SDK checkout has uncommitted edits, the pin avoids them |
| Built-in render pipeline, instanced procedural fish | Same as the SDK sample apps; no external assets needed |

## Phases

- [x] P0 Verify the model exists, find request/response + image format, measure latency
- [x] P0 Accuracy smoke test on 22 labelled images (`tools/bench_touch.py`)
- [x] P0 Unity project skeleton in `app/` (manifest, ProjectSettings + XR loaders from simple-app)
- [x] P0 Core logic (`ShoalingWithKin.Core`): request/response JSON, kin matcher (hysteresis) + EditMode tests (16/16 pass)
- [x] P0 Runtime: camera frame sampler, Luna client, touch-kin detector
- [x] P1 Fish shoal (boids, instanced, shader), soundscape (procedural music per kin), HUD (mono + stereo)
- [x] P0 Editor setup script: scene + player settings + key file + iOS build
- [x] P0 Verify: batchmode compile (0 warnings), EditMode tests 16/16, app-request bench 44/44, xcodebuild SUCCEEDED, installed on Holo iPhone 15 A
- [ ] P0 First launch on iPhone A — BLOCKED: SpringBoard denies launch ("profile has not been explicitly trusted"); same cert/profile as HoloKit Pinch, which launches. Needs a tap / trust on the phone
- [x] P0 Apply code-review findings (7/7: stuck in-flight on parse/handler exceptions, fish hidden after kin switch, MirrorY→MirrorX + HoloKit mono orientation lock, match timeout, linear colours, HUD label width, missing-outcome decay); tests 22/22, rebuilt + reinstalled
- [x] P1 README + findings doc
- [ ] Commit (waiting for the user to ask)

## Out of scope this pass (P2)
- Hand-centred crop of the frame (mapping verified, not yet needed by the accuracy data)
- Relay server to keep the API key off the device
- Field validation at the creek (needs the device + site)

## Blockers / risks
- `holokit-simple-app/DiagnosticsOverlay.cs` notes hand content not showing on iOS 27 — may affect the hand-position target
- API key ships inside the app bundle (StreamingAssets); use a dedicated, credit-limited key
