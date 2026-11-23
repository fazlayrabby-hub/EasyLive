# EasyLive — Handoff (read this first)

State as of the end of Task 9. Branch: `feature/easylive-v1` (GitHub: fazlayrabby-hub/EasyLive). Everything needed to continue is in this repo; nothing lives only on the first PC.

## Status
| Task | What | State |
|---|---|---|
| 1 | Models + QualityPlanner | done, reviewed |
| 2 | AdaptationController | done, reviewed |
| 3 | HardwareDetector | done, reviewed (AMD row in docs/hardware-validation.md still "pending") |
| 4 | NetworkProbe | done, reviewed |
| 5 | KeyStore + Platforms | done, reviewed (1 fix round) |
| 6 | BlankFrameDetector | done, reviewed |
| 7 | WGC capture (IFrameSource, WgcFrameSource) | done, reviewed (1 fix round) |
| 8 | AudioMixer | done, reviewed (1 fix round) |
| 9 | FfmpegStreamSession (encode + RTMPS send) | done, reviewed (1 fix round); QuickSync verified, **AMF/NVENC NOT verified** |
| 10 | SessionOrchestrator | **next** |
| 11 | WPF app (MainViewModel + window) | todo |
| 12 | E2E validation (Intel + AMD + YouTube + Facebook), packaging, licences | todo |

Specs/plan: `docs/superpowers/specs/2026-11-23-easylive-design.md`, `docs/superpowers/plans/2026-11-23-easylive.md`.
Process state (decisions, rulings, deferred minor findings, per-task implementer reports): `docs/superpowers/sdd-state/` — start with `progress-ledger.md`. (It mentions D:\ paths and a PowerShell/gh path of the first PC; ignore those.)

## Setup on a new PC
1. Install .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`). Optional: GitHub CLI (`winget install GitHub.cli`).
2. `git clone -b feature/easylive-v1 https://github.com/fazlayrabby-hub/EasyLive.git`
3. Tools are NOT in git (see `tools/README.md`): put the FFmpeg **LGPL shared** win64 build (BtbN FFmpeg-Builds, FFmpeg n8.1, matching FFmpeg.AutoGen 8.1.0) under `tools/ffmpeg/<folder>/bin`, and MediaMTX under `tools/mediamtx/mediamtx.exe` (MIT; the integration tests start it themselves on port 1935 and use `ffprobe` from the FFmpeg bin). Confirm the FFmpeg build is LGPL (no `--enable-gpl`, no libx264).
4. `dotnet test tests/EasyLive.Core.Tests` — expect ~167 pass, 4 skipped (manual), 0 fail. Encoder tests are skipped/limited by what `HardwareDetector` reports for that PC's GPU.

## Why the AMD PC matters (do these there, report results, do not edit code unless asked)
- Run the manual smoke tests (un-skip temporarily, do not commit): hardware detection (Task 3), monitor capture (Task 7), loopback+mic audio (Task 8).
- Run the streaming integration tests with `EncoderKind.Amf` (Task 9): valid H.264 High stream, keyframes ≤ 2.2 s, `SetBitrate` live (does AMF accept a live bitrate change or does it fall back to the re-open path?), `ReinitAsync`, unconsumed encoder options (log "unused options"), CPU use. Add the AMD row to `docs/hardware-validation.md`.
- Known unknowns: NVENC (no test machine, mark "untested"), RTMPS to a real platform.

## Key decisions (rulings) — details in sdd-state/progress-ledger.md
- Resolution fixed per session; only bitrate adapts live (re-init only as last resort).
- Distribution to others, free → LGPL-clean build; software fallback = libopenh264 (present in FFmpeg 8.1 build; adds extra keyframes and can overshoot the bitrate on busy content; `h264_mf` is a one-line alternative in `VideoEncoder.CodecNameFor` if Facebook rejects it).
- `CaptureTargets.List()` replaces the static interface member `IFrameSource.ListTargets` — Task 11 must call `CaptureTargets.List()`.
- WGC emits frames only on screen change → the stream session repeats the last frame to hold constant fps (done in Task 9).
- `PushVideo/PushAudio` are non-blocking (AudioMixer.Mixed handlers run under a lock).
- v1 frame path is CPU conversion (staging texture → libswscale → NV12 → hw encoder), ~22% of one core at 720p30; GPU-resident path is a later optimisation.
- Task 10 must: subscribe to `AudioMixer.Notice` BEFORE `Start()`; handle `IStreamSession.Failed`; wire reconnect (retry every 5 s for up to 2 min), black-frame warning, capture-closed message, and feed `StreamHealth` to `AdaptationController` once a second (`SetBitrate` / `ReinitAsync`).

## Working rules used so far
- Subagent-driven development: one implementer per task + task review (+ scoped re-review after fixes). Never leave failing tests committed.
- Commit identity used: `EasyLive Dev <dev@easylive.local>`; commit messages Conventional Commits; push to `feature/easylive-v1` only (no force push); do not merge to `master` without the owner's OK.
- Only one PC should commit to the branch at a time — `git pull` before starting.

## Prompt to paste into Claude Code on the second PC
> Read docs/superpowers/HANDOFF.md, then docs/superpowers/sdd-state/progress-ledger.md. Do not redo Tasks 1–9. First do the AMD checks listed in the handoff on this PC and report results; do not change code until I say so.
