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
