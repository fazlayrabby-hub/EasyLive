# Hardware validation

Manual `HardwareDetector.Detect(new DxgiAdapterSource())` results (smoke test `Manual_smoke_prints_real_hardware`).

| PC | GPU name | Encoders detected | Date |
|----|----------|-------------------|------|
| This PC (dev) | Intel(R) Iris(R) Xe Graphics | QuickSync, OpenH264 | 2026-11-23 |
| AMD PC (Ryzen 5 5600X, 12 logical cores) | AMD Radeon RX 6700 XT (driver 32.0.21030.2001; also "Microsoft Basic Render Driver" listed) | Amf, OpenH264 | 2026-11-23 |
| NVIDIA | untested (no test machine) | — | — |

## AMD PC — other manual / integration checks (2026-11-23)

- **Monitor capture (WGC, Task 7):** primary monitor 1920x1080, 403 frames in 3 s, 0 size mismatches; border `IsBorderRequired=False` (readback).
- **Audio (Task 8):** loopback + mic mixed for 3 s; mic present, `MicActive=True`, ~3.06 s audio in 3.07 s wall, no odd-length chunks.
- **Streaming with `h264_amf` (Task 9, local MediaMTX):**
  - 720p30 10 s with 3 s freeze: H.264 High 1280x720, 30.00 fps, AAC 48 kHz stereo, keyframes every 2.00 s, max video gap 34 ms; CPU ~1% of one core (OpenH264 fallback: ~8%).
  - `SetBitrate` 2500 → 1000 kbps: 2113 → 1035 kbps observed, same connection. AMF is not in `VideoEncoder.SupportsLiveBitrate`, so it always takes the re-open path (1 re-open, one extra IDR ~0.10 s after the change). A true live change on AMF was not tried.
  - `ReinitAsync` 720p → 480p: 78 ms, same connection, H.264 High 854x480, keyframes ≤ 2.00 s.
  - Encoder options: `h264_amf: all options accepted` on every open (ffmpeg.log). The `Encoder_accepts_every_option_we_set` test has no `h264_amf` case.
  - ffprobe reports AMF `avg_frame_rate=1000/33` (packet timing is 30.00 fps) — check against YouTube/Facebook in Task 12.
- RTMPS to a real platform: not tested.

## Task 12 validation checklist (manual, to be filled in by a person)

Nothing below has been run yet. Leave a cell empty until it has actually been measured; do not copy numbers from the local-MediaMTX tests above.
Use the packaged app (`artifacts\EasyLive-<version>\EasyLive.exe`, built by `build\publish.ps1`) and a YouTube *unlisted* test broadcast / a Facebook test live video.
Pass criteria: no visible stutter, dropped frames < 0.5 %.

| # | Step | PC / GPU / encoder used | Date | Duration | Rung picked (auto) | Frames dropped (%) | Game/video FPS impact | Stutter seen? | Pass / Fail | Notes |
|---|------|-------------------------|------|----------|--------------------|--------------------|-----------------------|---------------|-------------|-------|
| 1 | 10-min YouTube unlisted run, Intel PC (Iris Xe, QuickSync) | | | | | | | | | |
| 2 | 10-min YouTube unlisted run, AMD PC (RX 6700 XT, AMF) | | | | | | | | | |
| 3 | 3 Mbps upload throttle with `clumsy` mid-stream (YouTube) | | | | | | | | | |
| 4 | 10-min Facebook test live video | | | | | | | | | |
| 5 | NVENC (NVIDIA PC) | **untested** (no test machine) | | | | | | | | |

Step 3 details to record (expected: bitrate steps down within ~3 s, no freeze, recovers after ~20 s):

| Throttle started (time) | Time bitrate first stepped down (s after start) | Lowest bitrate (kbps) | Freeze seen? | Time to recover after throttle removed (s) | Final bitrate (kbps) | Pass / Fail |
|---|---|---|---|---|---|---|
| | | | | | | |

Other things to note while testing: YouTube/Facebook stream-health page (keyframe interval 2 s, no warnings), the AMF `avg_frame_rate=1000/33` ffprobe oddity (see above), and that the software fallback (OpenH264) was *not* the encoder in use unless that is what is being tested.

Packaging checks (see `docs/superpowers/sdd-state/task-12-report.md`): the published `EasyLive.exe` was smoke-launched on the dev PC (which does have a system .NET installed, with `DOTNET_ROOT` unset) and FFmpeg was resolved from `ffmpeg\`. Not yet checked on a clean PC with no .NET installed: please unzip the package there, start `EasyLive.exe` and note the result here: ________
