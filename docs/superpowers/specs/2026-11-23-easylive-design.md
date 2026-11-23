# EasyLive — Design

Working name: **EasyLive** (my call; rename freely).

## Intent and the why
A Windows app that live-streams the screen or a game to YouTube or Facebook, for **non-technical users** who don't know what to configure. OBS and Streamlabs exist but expose too many settings. EasyLive's whole point: the app inspects the PC and the internet connection, chooses the settings itself, and keeps the stream smooth. Personal/free tool, not for sale.

## Goals
- Go live in ~3 clicks: pick what to share → pick YouTube/Facebook and paste stream key (one time) → **Go Live**.
- No settings the user must understand (no bitrate, encoder, resolution, fps controls in the main flow).
- Auto-detect PC capability (GPU, hardware encoder, CPU) and pick encoder + resolution + fps.
- Auto-measure upload speed/stability to the platform's own ingest server and pick bitrate and quality.
- Stay smooth: adapt quality live (bitrate/resolution) when the connection degrades, recover when it improves. Minimal impact on the game's own frame rate (hardware encode).
- Capture screen/monitor or a specific window/game, plus system audio and microphone.

## Non-goals (v1)
Scenes, overlays, webcam compositing, chat, recording to file, multi-streaming, alerts/widgets, plugins, advanced settings panel, macOS/Linux, exclusive-fullscreen game capture (users run games in borderless/windowed; the app tells them if needed).

## Anti-goals (failure even if technically working)
- A settings screen the user must understand.
- Stutter or dropped frames on the stream, or noticeable slowdown of the game.
- Silent failure: if the stream can't start or drops, the user sees a plain-language message and what to do.

## Constraints
- Windows 10 2004+/11 (required by Windows Graphics Capture).
- Free/non-commercial; use only LGPL-compatible dependencies (FFmpeg LGPL shared build with NVENC/AMF/QSV, dynamically linked).
- YouTube and Facebook both ingest RTMP(S) with a server URL + stream key; v1 uses pasted keys, no OAuth.

## Approaches considered
- **A. C# app driving `ffmpeg.exe`:** fastest to build, but bitrate can't change live (restart = visible hiccup), weaker game capture. Rejected.
- **B. C# app with FFmpeg libraries in-process + Windows Graphics Capture — CHOSEN by user.** Hardware encode, live bitrate/resolution changes without restart, no GPL issue. More work.
- **C. Simple UI over the OBS engine:** best game capture, but GPL, heavy bundle, limits how simple the app can be. Rejected.

## Decided parts (the how)
User-decided: approach B; not for sale.
My calls (not agreed with the user, flag if wrong):
- Language/UI: **C# / .NET 8 with WPF** (simpler packaging than WinUI 3; plenty for a minimal UI).
- Capture: **Windows Graphics Capture** (monitor and window), frames kept on the GPU where possible.
- Encode/mux/send: **FFmpeg libraries via FFmpeg.AutoGen**, H.264 with NVENC, AMF or QuickSync; x264 `veryfast`-or-lighter CPU fallback.
- Audio: **WASAPI** loopback (system) + microphone, mixed, AAC.
- Auto-setup: GPU/encoder + CPU detection (via DXGI/WMI), then a short upload probe to the chosen platform's ingest endpoint (throughput, jitter, loss) → pick a quality "ladder" rung (e.g. 1080p60 / 1080p30 / 720p60 / 720p30 / 480p30) with ~70–75% of measured upload as the bitrate ceiling.
- Adaptation loop: monitor encoder queue + send backlog/dropped frames; step bitrate/resolution down fast, up slowly.
- Stream keys stored locally, encrypted with Windows DPAPI.
- UI: one window, large **Go Live** button, status line (Live / quality / health in plain words), Stop button.

## Calls made after the builder check (mine, not agreed with the user — flag if wrong)
- **Resolution is fixed per session; only bitrate (and fps as a last resort) adapts live.** Facebook is strict (2 s keyframe interval, fixed settings) and H.264 resolution changes need a new SPS/IDR. The ladder rung (resolution/fps) is chosen once at Go Live; mid-stream the app only varies bitrate. If bitrate alone can't hold, a brief re-init with a new keyframe is allowed (one-second blip) — never a silent freeze.
- **Server URL:** hard-coded per platform preset (YouTube `rtmps://a.rtmps.youtube.com:443/live2`, Facebook `rtmps://live-api-s.facebook.com:443/rtmp/`); user supplies only the key. Facebook keys expire, so the UI says "paste the new key from Facebook Live Producer" when rejected.
- **Upload probe:** no fake data is pushed to the real ingest. Probe = TCP connect time/jitter to the ingest host + short throughput test to a neutral speed server (e.g. Cloudflare), max ~8 s, run before Go Live. Start at a conservative rung (~60% of measured upload), then adapt up.
- **Black/blank capture:** detect-and-warn (sampled frames all black for >3 s) with plain advice (use borderless/windowed, run as same user). DXGI desktop-duplication fallback for full-monitor capture only.
- **Audio:** system loopback of all apps + microphone, both on by default, fixed levels in v1 with a single mute button for each; A/V sync via shared QPC timestamps.
- **Reconnect:** auto-retry for up to 2 minutes with a "Reconnecting…" status, then stop and explain.
- **Higher than 1080p displays:** downscale to the chosen rung. Multi-monitor: user picks the monitor. Single-instance app.
- **Distribution (user-decided):** the app will be given to other people for free, not sold. So the whole build stays LGPL-clean: **no x264/GPL**. The CPU fallback is **OpenH264** (Cisco binary, BSD) loaded as a DLL; it is weaker, so the fallback rung is capped lower (e.g. 720p30). The app ships the FFmpeg LGPL DLLs with the licence text and a "third-party notices" file.
- **Validation hardware (user-provided):** two PCs: Intel Iris Xe (Quick Sync) and an AMD GPU PC (AMF). Both paths must be verified before release. NVENC is best-effort (no test machine) and may be shipped as "untested" until someone with an NVIDIA card confirms it.

## Left to the builder
Project/solution layout, exact thresholds of the quality ladder and adaptation timings, probe method details, error-message wording, installer/packaging, test strategy (including a local RTMP test sink such as MediaMTX), logging.
