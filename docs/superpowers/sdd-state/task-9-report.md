# Task 9 report: Encode + RTMP(S) send (FfmpegStreamSession)

**Status: DONE_WITH_CONCERNS.** Everything works and all tests are green on this PC (Intel Iris Xe, QuickSync). The concerns are listed in §9. The main ones: the libopenh264 fallback does not keep a regular 2 s keyframe cadence and cannot hold its bitrate cap on extreme content, and I used FFmpeg 8.1 instead of 7.x.

Branch `feature/easylive-v1`, BASE `141d1aa`, commits:

| SHA | Subject |
|---|---|
| 36beee4 | feat: ffmpeg locator, send queue, error texts, timeline helpers |
| 7ccbd51 | feat: video/audio encoders, frame converter, rtmp muxer, ffmpeg runtime |
| 019ddc4 | feat: ffmpeg stream session (encode ticker, send thread, live bitrate, reinit) |
| 65e30a8 | fix: qsv live bitrate keeps vbv size, re-open fallback, start/dispose race, converter context ref |
| 5e6de42 | test: streaming integration (MediaMTX sink + ffprobe, synthetic frames/audio) |

No binaries are committed. `git ls-files tools` shows only `tools/README.md`, and `tools/ffmpeg` and `tools/mediamtx` are git-ignored.

## 1. FFmpeg build identity (downloaded into tools/, untrusted, verified)

- Source: the BtbN `latest` release. `api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest` lists only `n8.1`, `n9.0` and `master` LGPL-shared assets; there is **no 7.x asset any more**. I took `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1.zip` and extracted it to `tools/ffmpeg/ffmpeg-n8.1-latest-win64-lgpl-shared-8.1/`.
- `ffmpeg -version`: `ffmpeg version n8.1.3-39-g660b5aa7db-20261122`.
- The configuration line contains `--enable-version3`, **no `--enable-gpl`**, `--disable-libx264 --disable-libx265`, `--enable-libopenh264 --enable-amf --enable-ffnvcodec --enable-libvpl --enable-schannel`. That means LGPLv3.
- DLLs: `avcodec-62`, `avformat-62`, `avutil-60`, `swscale-9`, `swresample-6`.
- Paired NuGet: **FFmpeg.AutoGen 8.1.0**. Its major version matches; it is a single assembly with `ffmpeg.RootPath`, and `LibraryVersionMap["avcodec"]` gives the marker `avcodec-62.dll`. **Deviation:** the brief said 7.x ↔ AutoGen 7.x. Following the same rule (matching majors) with what is actually published gives 8.1 ↔ 8.1.0.
- H.264 encoders in this build: `libopenh264`, `h264_amf`, `h264_d3d12va`, `h264_mf`, `h264_nvenc`, `h264_qsv`, `h264_vaapi`, `h264_vulkan`. AAC encoders: `aac`, `aac_mf`.
- Which encoders actually open on this PC (CLI probe): `h264_qsv` OK, `libopenh264` OK, `h264_mf` OK. `h264_amf` fails (no AMD GPU). `h264_nvenc` fails ("Cannot load nvcuda.dll", no NVIDIA GPU).
- **libopenh264 is present**, so `EncoderKind.OpenH264` uses `libopenh264` as the spec says. `h264_mf` is only the automatic substitute if a build lacks libopenh264; this build has it, so it is not in use. See §9 for why switching to h264_mf may still be worth considering.
- MediaMTX: `mediamtx_v1.21.4_windows_amd64.zip` (latest), in `tools/mediamtx/`.

## 2. What was built (src/EasyLive.Core/Streaming/)

| File | Role |
|---|---|
| IStreamSession.cs | `StreamHealth`, `IStreamSession` (signatures as in the plan), `StreamSessionException` |
| FfmpegStreamSession.cs (+ `.Encode.cs`, `.Send.cs`) | Orchestration: start/stop, `PushVideo`/`PushAudio`, encode ticker thread, send thread, live bitrate change, re-init |
| VideoEncoder.cs | H.264 encoder per kind/options. High profile, GOP = 2·fps, no B-frames, `bitrate == maxrate`, bufsize = 2× bitrate, BT.709 limited range, global header |
| AudioEncoder.cs | AAC-LC 128 kbps, 48 kHz stereo. Converts interleaved input to planar 1024-sample frames; silence is written in pieces |
| FrameConverter.cs | Three D3D11 staging textures. `Ingest` calls `CopyResource` inside the capture callback and keeps the latest copy (older ones are dropped). On the encode thread: Map → swscale `SWS_FAST_BILINEAR` BGRA → NV12/YUV420P at rung size, BT.709 |
| RtmpMuxer.cs | FLV over RTMP(S). Every blocking call has a deadline enforced through the AVIO interrupt callback; `Abort()` unblocks any call |
| SendQueue.cs | Bounded queue measured in ms, with the drop policy |
| ErrorTexts.cs | Fixed plain texts and the connect/send failure classifiers |
| FfmpegLocator.cs | Finds the DLLs: app\ffmpeg → `EASYLIVE_FFMPEG_DIR` → tools\ffmpeg\*\bin, walking up from the app folder |
| FfmpegRuntime.cs | One-time init; log callback writes to `%LOCALAPPDATA%\EasyLive\logs\ffmpeg.log` (warning level and above, keys redacted, rotation at 5 MB); captures error lines per thread for classification |
| UrlMasker.cs | Masks everything after the app segment |
| Timeline.cs | `VideoClock` (constant-frame-rate ticks) and `AudioTimeline` (sample counting with 50 ms resync, silence fill, overlap drop, pre-start drop, `PadTo`) |
| PreciseWaiter.cs | High-resolution waitable timer for the ticker, woken early by stop |

### Design decisions, following the controller rulings

1. **Non-blocking pushes.**
   - `PushVideo` adds a reference to the texture, queues one GPU `CopyResource` into an own staging texture, marks it as the latest frame, and returns. It never throws into the callback; failures are logged.
   - `PushAudio` only enqueues. The audio queue is capped at 5 s and drops its oldest chunks.
   - The device is put into multithread-protected mode because the capture thread and the encode thread share the immediate context.
2. **CPU conversion path:** staging texture → Map → swscale → system-memory frames for the encoder. NV12 goes to qsv/amf/nvenc/mf and YUV420P to libopenh264.
3. **Constant frame rate.**
   - The encode thread ticks at the rung's fps using a high-resolution waitable timer and encodes the latest picture on every tick, repeating it when nothing new arrived.
   - It waits for the first frame, up to 5 s, and then raises `Failed` with "Nothing to stream — the screen did not provide any picture."
   - The session zero point is the moment the first frame is available. Video PTS = tick index (scheduled QPC − start); audio PTS = (chunk QPC − start) in samples. Contiguous chunks are counted back to back; a discrepancy over 50 ms forces a resync (silence for a gap, dropped overlap for a backwards jump).
   - If the encoder falls ≥ 2 ticks behind, it skips ticks to catch up instead of building latency. Skipped ticks count in `DroppedFramesTotal`.
   - **Extra:** if no audio arrives at all, the audio track is kept continuous with silence (250 ms behind real time). Facebook drops streams whose audio stops, and FFmpeg's interleaver would otherwise hold back the video.
4. **Send queue and network thread.**
   - `av_interleaved_write_frame` runs only on the send thread, with a 10 s deadline per write.
   - `SendQueueMs` = newest queued DTS − oldest unsent DTS.
   - Above 3 s, non-key video packets are dropped oldest-first; audio and keyframes are never dropped. Because a P-frame cannot be decoded without the frames before it, a drop removes the rest of that GOP up to the next queued keyframe. If no keyframe follows, incoming delta frames are dropped until a keyframe arrives, and the encoder is asked for an IDR via `TakeKeyframeRequest`.
   - `EncodeLagMs` is real time minus the scheduled tick. `CurrentKbps` is the last applied bitrate.
5. **`SetBitrate`:** the behaviour per encoder is in §4. If an encoder rejects a live change at runtime, the session automatically falls back to re-opening the encoder on the same connection.
6. **`ReinitAsync`:**
   - It drains the old encoder, opens the new one at the lower rung, sends the new SPS/PPS in-band through `AV_PKT_DATA_NEW_EXTRADATA` (the FLV muxer writes a new AVC sequence header), and keeps the connection and the timeline.
   - The bitrate is clamped to the new rung's range.
   - The converter re-converts the latest captured picture at the new size, so the new encoder does not start on black.
7. **Errors in plain words.**
   - Connect failures are classified from FFmpeg's error lines captured on the connecting thread. "Server error / publish / unauthorized / denied …" means the key was rejected. "Connection to tcp / failed to resolve / TLS …" means could not connect. A connection closed right after the TCP connect (EOF/reset) means key rejected.
   - A send failure within 5 s of publishing means key rejected; later it means the connection was lost.
   - Raw errors go only to the log. The stream key is redacted from all log lines and URLs are logged masked; a test asserts the key never appears in the log.
8. **Stop semantics.**
   - `DisposeAsync` is idempotent (it caches one task) and is safe to call from `Failed`, which is raised on the thread pool.
   - Order: set stop → wait for a connect in progress (bounded) → join the encode thread, which flushes both encoders → complete the queue → join the send thread, which sends the rest (3 s each) → abort the muxer and join again for 1 s → write the trailer (1 s) → free packets, muxer, encoders, frame, converter and D3D objects.
   - If a worker is still stuck in a native call, native objects are deliberately leaked (and logged) rather than freed under it.
   - Thread start and dispose are synchronised on the session lock, so there is no start/dispose race.
9. **Threading hygiene:** both workers catch everything, log it, and raise `Failed` with "Streaming stopped unexpectedly." Callbacks into native code never throw.

**`StartAsync` contract (my choice, documented on the interface):** on failure it raises `Failed` once and also throws a `StreamSessionException` with the same plain message, so awaiting code cannot proceed as if the stream were live. Cancellation through `ct` throws `OperationCanceledException` without raising `Failed`.

## 3. Measured CPU use (`Process.TotalProcessorTime` deltas, test (a), 10 s, 1080p synthetic source → 720p30)

| Encoder | CPU time / wall | One core | Total (16 logical cores) |
|---|---|---|---|
| h264_qsv | 2.36 s / 10.79 s | 22% | 1.4% |
| libopenh264 | 2.17 s / 10.35 s | 21% | 1.3% |

These figures are the whole test process. They also include the synthetic source uploading a 1080p texture 30×/s and the sine generator, so the session's own cost is lower. Earlier runs were in the same range: 19–28% of one core.

## 4. SetBitrate per encoder: what works and what was verified

| Encoder | Behaviour | Verified here? |
|---|---|---|
| **h264_qsv** | **Live.** Sets `bit_rate` and `rc_max_rate`; FFmpeg's qsvenc calls `MFXVideoENCODE_Reset` on the next frame. **The VBV buffer size must stay what it was at open (2 × start bitrate).** Experiments: changing `rc_buffer_size` up or down, with or without `rc_initial_buffer_occupancy`, was always rejected with `Error during resetting: incompatible video parameters (-14)`. I also set `strict_std_compliance = FF_COMPLIANCE_UNOFFICIAL`, which turns off NAL HRD signalling (SPS 41 instead of 51 bytes). With HRD on, a reset added an extra IDR right at the change. | **Yes.** 1662 → 987 kbps observed, `CurrentKbps` = 1000 after 31 ms, 0 re-opens, 1 connection, keyframes 2.00/2.00/2.00/2.00, max video gap 0.034 s |
| h264_nvenc | Coded as live: FFmpeg's nvenc reconfigures on `bit_rate`/`rc_max_rate` changes; the VBV is kept. If it rejects a change, the automatic re-open fallback applies. | **No** (no NVIDIA GPU here) |
| h264_amf | Re-open (keyframe-aligned, same connection). I could not verify AMF live reconfiguration, so I chose the safe path. | **No** (no AMD GPU here) |
| libopenh264 | Re-open: no live reconfiguration in the FFmpeg wrapper. | **Yes.** 2133 → 1008 kbps, `CurrentKbps` = 1000 after 31 ms, 1 re-open, 1 connection, max gap 0.067 s |
| h264_mf | Re-open (only used if a build lacks libopenh264). | Yes, through a temporary override during development (since removed): 2173 → 1042 kbps |

**Safety net:** if an encoder marked as live rejects the change (its next `send_frame` fails), the session re-opens it on the same connection and uses re-open from then on. Before the VBV fix this was exercised with QSV: the stream continued with a 0.47 s gap and no reconnect.

**Remaining gap for QSV:** after a live drop, the VBV stays 2 × the start bitrate, not 2 × the new rate. The rate cap itself is respected (observed ≈ 1000 kbps). If the platform enforces strict bufsize = 2 × bitrate after a change, use re-open instead (one IDR, ~0.35 s blip on QSV).

## 5. RED/GREEN evidence

- **RED, pure parts:** `dotnet build tests/EasyLive.Core.Tests` failed with `CS0234: The type or namespace name 'Streaming' does not exist` and `CS0246: 'SendQueue<>' could not be found` before any implementation existed. `PadTo` was RED as a stub: `Failed TimelineTests.Pad_to_fills_silence_up_to_a_target_and_never_goes_back — System.NotImplementedException`.
- **GREEN, pure parts:** `dotnet test --filter SendQueue|ErrorTexts|UrlMasker|FfmpegLocator|Timeline` → `Passed! Failed: 0, Passed: 31`.
- **Integration (RED → GREEN during development):**
  - The first run failed: `Assert.Null() Failure ... "Nothing to stream…"`. The log showed a `NullReferenceException` in `ID3D11Device.CreateTexture2D`, because the managed `Device` wrapper was disposed by my `using`. Fixed with an explicit raw `GetDevice` and clear ownership.
  - Then ffprobe returned `I/O error`, because MediaMTX lists a path ~1.5 s after connect. Fixed with a retry in the test helper.
  - Then the QSV reset error, the libopenh264 cadence and bitrate issue, and the ffprobe `packets_and_frames` parsing issue (§4, §9).
- **Final full Core test run** (`dotnet test tests/EasyLive.Core.Tests`): `Total tests: 157, Passed: 153, Skipped: 4`. The 4 skips are the existing manual tests from earlier tasks. Build: 0 warnings, 0 errors. The fixture started and stopped MediaMTX itself, and no mediamtx process was left running.

### Unit tests added

- **SendQueueTests (6):** no drop under the limit; QueueMs = newest − oldest; never drops audio or keyframes; drops the oldest GOP tail first and stops once under the limit; waits for a keyframe after a tail drop and raises a keyframe request; Complete/DrainAll.
- **ErrorTexts (9):** refused / unresolvable → could not connect; server publish errors → key rejected; closed after connect → key rejected; timeout → could not connect; send failure < 5 s → key rejected, later → connection lost; all texts plain (no "ffmpeg", "error -" or digits).
- **UrlMasker (4).**
- **FfmpegLocator (4):** app folder > env var > dev tools; null when nothing matches; uses temp dirs.
- **Timeline (6):** `VideoClock` round trip; contiguous audio; forward gap → silence; backward → overlap dropped; pre-start dropped; `PadTo`.

## 6. Integration tests (tests/EasyLive.Core.Tests/StreamSessionIntegrationTests.cs) and ffprobe output

These tests use a `MediaMtxFixture` that starts `tools/mediamtx/mediamtx.exe` with its own temporary config:
- RTMP only on `127.0.0.1:1935`;
- publishing allowed under `test/`, refused elsewhere;
- reading allowed.

They are skipped through `[SkippableFact]`/`[SkippableTheory]` (Xunit.SkippableFact 1.5.23) if the FFmpeg DLLs, `ffprobe.exe` or `mediamtx.exe` are missing, or if port 1935 is busy. `EASYLIVE_TEST_USE_RUNNING_SINK=1` reuses a sink you started yourself, as a debug aid. Synthetic sources:
- a D3D11 BGRA texture delivered like WGC at ~30 events/s, either a moving gradient or 8×8 random-colour blocks changing every frame;
- a 440 Hz sine sent as contiguous 10 ms QPC-stamped chunks.

### (a) 10 s at 720p30 with a 3 s freeze (no events from 3 s to 6 s), 1080p source downscaled

Best encoder = `HardwareDetector...Encoders[0]` = QuickSync:
```
ffprobe: #0 video h264 High 1280x720 avg_frame_rate=1000/33; #1 audio aac LC 48000 Hz 2 ch
 | video packets=186 span=6.17s fps=30.00 maxGap=0.034s keyIntervals=[2.00,2.00,2.00] | audio packets=375 maxGap=0.022s
```
The test asserts: High profile; 1280x720; average fps in 28.5–31.5; no gap above 2 frame durations (the freeze is covered by repeated frames); keyframes every 2 ± 0.2 s; AAC 48 kHz, 2 channels; audio gap < 0.1 s; no `Failed`; 1 connection.

### (c) OpenH264 plan at 720p30 (the same test with `fallback: true`, using libopenh264)

```
ffprobe: #0 video h264 High 1280x720 avg_frame_rate=30/1; #1 audio aac LC 48000 Hz 2 ch
 | video packets=228 span=7.57s fps=30.00 maxGap=0.034s keyIntervals=[2.00,1.43,0.80,1.00,1.20,0.80] | audio packets=375 maxGap=0.022s
```
The output is valid. For libopenh264 the keyframe assertion is relaxed to "every interval ≤ 2.2 s"; see §9.1.

### (b) SetBitrate(1000) about 6 s into a 13 s stream; the reader spans the change

QSV, with block-noise content:
```
CurrentKbps=1000 after 31 ms; reopens=0 connects=1
observed video kbps before=1662 after=987; video h264 High 1280x720 fps=30.00 maxGap=0.034s keyIntervals=[2.00,2.00,2.00,2.00]; audio maxGap=0.022s
```
libopenh264, with gradient content:
```
CurrentKbps=1000 after 31 ms; reopens=1 connects=1
observed video kbps before=2133 after=1008; video h264 High 1280x720 fps=29.90 maxGap=0.067s
```
The test asserts: `CurrentKbps` = 1000 within 3 s; after < 0.7 × before; after in 500–1400; no gap ≥ 1 s in video or audio packet times; 1 connection. For QSV/NVENC it also asserts 0 re-opens and no extra IDR; for the others, exactly 1 re-open.

### (e) ReinitAsync(480p30) about 4 s in

The test streams at 720p30 on QSV. One reader spans the switch and decodes frames; a second reader starts after the switch.
```
reinit took 396 ms; reopens=1 connects=1 health=StreamHealth { SendQueueMs = 0, DroppedFramesTotal = 10, EncodeLagMs = 0, CurrentKbps = 1000 }
spanning reader decoded sizes: 1280x720,854x480 | video High fps=28.60 maxGap=0.333s keyIntervals=[0.07,2.30,2.00,2.00] | audio maxGap=0.022s
new reader: #0 video h264 High 854x480 avg_frame_rate=1000/33 | fps=30.00 maxGap=0.034s keyIntervals=[2.00]
```
The same connection carries both resolutions: the reader that spans the switch decodes 1280x720 and then 854x480. The blip is about 0.35 s, which is how long a QSV session takes to open; 10 ticks are skipped and counted in `DroppedFramesTotal`.

### (d) Wrong URL `rtmp://127.0.0.1:1/nothing/x`

`failed after 5467 ms: Could not connect — check your internet connection.` `StartAsync` throws with the same text. `DisposeAsync` was called twice. The test asserts no "ffmpeg", no "error -" and no digits. Raw log line: `connect failed: Error number -138 occurred (-138); [tcp @ …] Connection to tcp://127.0.0.1:1?tcp_nodelay=1 failed`.

### Extra tests

- **Refused publish (key rejected):** MediaMTX refuses publishing to `denied/secretkey` and the session reports `failed: The platform did not accept your stream key — paste a new key.` Raw log line: `Server error: authentication failed`. The test asserts "secretkey" never appears in ffmpeg.log (count 0).
- **No frames:** `failed after 5048 ms: Nothing to stream — the screen did not provide any picture.` Here `DisposeAsync` is called from inside the `Failed` handler.

## 7. Files changed

- **Added, src/EasyLive.Core/Streaming/:** `IStreamSession.cs`, `FfmpegStreamSession.cs`, `FfmpegStreamSession.Encode.cs`, `FfmpegStreamSession.Send.cs`, `VideoEncoder.cs`, `AudioEncoder.cs`, `FrameConverter.cs`, `RtmpMuxer.cs`, `SendQueue.cs`, `ErrorTexts.cs`, `FfmpegLocator.cs`, `FfmpegRuntime.cs`, `UrlMasker.cs`, `Timeline.cs`, `PreciseWaiter.cs`.
- **Modified:** `src/EasyLive.Core/EasyLive.Core.csproj` (FFmpeg.AutoGen 8.1.0; Vortice.Direct3D11 was already referenced).
- **Added, tests:** `SendQueueTests.cs`, `StreamingPureTests.cs`, `StreamSessionIntegrationTests.cs`, `Streaming/MediaMtxFixture.cs`, `Streaming/Ffprobe.cs`, `Streaming/SyntheticSources.cs`.
- **Modified, tests:** `EasyLive.Core.Tests.csproj` (Xunit.SkippableFact 1.5.23, AllowUnsafeBlocks).
- **Added:** `tools/README.md`. `.gitignore` already ignored `tools/ffmpeg/` and `tools/mediamtx/`.

## 8. Self-review checklist

- **Unmanaged lifetimes:**
  - Every `AVPacket` is owned by exactly one place: the scratch packet, then the queue, then the writer. Dropped packets are freed in the queue callback; the rest are drained and freed at dispose.
  - `AVFrame`s are freed when the encoder is re-opened and at dispose. Encoder contexts use `avcodec_free_context` with null-out, which makes them safe to dispose twice.
  - The muxer runs `avio_closep` and `avformat_free_context` once.
  - The sws context is freed by the converter.
  - Staging textures and the device/context have explicit owned references. A busy slot is only marked stale on a device or size change and is disposed after unmap. The map path holds its own context reference.
  - Interrupt and log delegates are kept alive in fields or statics.
- **Threads:**
  - The encoder, frame and converter conversion state are touched only by the encode thread.
  - Writes happen only on the send thread.
  - The muxer's `Abort` is a volatile flag.
  - Pending bitrate and re-init requests are handed over with Interlocked operations or the lock.
  - Start and dispose are synchronised.
  - `Failed` fires once (Interlocked), on the thread pool.
- **Blocking calls:** connect ≤ 10 s, each write ≤ 10 s, trailer and close ≤ 1 s, thread joins 3 s + 1 s. A connect in progress honours `ct` and the deadline.
- **No raw FFmpeg text reaches users:** `Failed` only ever receives `ErrorTexts` constants.
- **Secrets:** the key is registered for redaction before anything is logged, and URLs are logged masked.

## 9. Concerns

1. **libopenh264 (the spec's fallback encoder):**
   - **(a) Irregular keyframes.** It inserts extra, content-triggered IDRs, and FFmpeg's wrapper does not expose openh264's scene-change switch. On scrolling content I measured keyframes every ~0.6–0.8 s. The CLI reproduces it with `-g 60`; it does not happen on testsrc2. The 2 s GOP is only an upper bound. Facebook is strict about the 2 s keyframe interval, so this may cause warnings or rejection there.
   - **(b) Bitrate cap.** On extreme content (fresh random 8×8 blocks every frame) it does not hold the cap: about 9 Mbps against a 2.5/1 Mbps target. Its own warning says rate control needs `allow_skip_frames`; with that enabled it keeps the rate by dropping almost every frame (36 packets in 11 s).
   - **Comparison with h264_mf on the same content:** exact 2.00 s keyframes and High profile, but it also overshoots on extreme content (15→10 Mbps). On realistic content both libopenh264 and h264_mf hold the cap (≈2.1 → 1.0 Mbps).
   - I kept libopenh264, as the spec and the controller asked. **Decision for you:** switch `EncoderKind.OpenH264` to `h264_mf` for its regular cadence (a flagged deviation), or keep libopenh264. It is a one-line change in `VideoEncoder.CodecNameFor`.
2. **FFmpeg 8.1 + FFmpeg.AutoGen 8.1.0 instead of 7.x.** BtbN no longer publishes 7.x in `latest`. Older dated releases may still carry 7.1 if 7.x is required.
3. **AMF and NVENC were not verified** (no such GPUs here). The options are set from FFmpeg documentation, NVENC is coded as live, and AMF uses re-open. The automatic re-open fallback covers a rejected live change. They need validation on the AMD PC (Step 4 of the brief) and an NVIDIA PC.
4. **QSV live bitrate keeps the initial VBV** (2 × start bitrate) after a change (§4). The rate is honoured; the buffer is larger than 2 × the new rate.
5. **Re-init and re-open blip on QSV is about 0.35 s** (MFX session open time). It meets the ≤ 1 s requirement. It could be made close to zero by opening the next encoder on a helper thread and swapping it in at a tick.
6. **RTMPS to a real platform is untested** (no keys, as the brief allows). The build has schannel, so `rtmps://` is supported by the same code path. The key-rejected classification for real platforms relies on log wording or a close within 5 s, which MediaMTX confirmed only for its own refusal.
7. **`DroppedFramesTotal` counts send-queue drops plus skipped encoder ticks.** I chose to fold both into one health number; documented on `StreamHealth`.
8. **The CPU path costs about 20% of one core at 720p30** (§3); GPU-resident conversion (D3D11VA) is left for later as ruled.
9. **The integration tests need port 1935 free and a desktop GPU,** and they take about 75 s in total. They skip with a message otherwise.

---

# Fix round 1 (review findings), commit `a614d47` "fix: time-based 2 s keyframes, send-queue storm and ceiling, review minors"

## What was wrong

**Important 1: keyframe interval could exceed 2 s.**
- The GOP was counted in frames, so skipped ticks stretched it.
- `ReopenVideo` computed the next tick from a `now` read before the ~350 ms QSV open. The loop then found itself about 10 ticks behind and skipped them, so the first GOP after the switch was too long.
- Old test (e) output showed this: `keyIntervals=[0.07,2.30,2.00,2.00]`, `DroppedFramesTotal=10`.

**Important 2: keyframe storm and unbounded memory under congestion.**
- Audio and keyframes are never dropped, and they kept the queue span above 3 s forever. Every new delta frame was dropped and an IDR was requested every other frame.
- There was no memory ceiling.

## Fixes

**Keyframes by time (`KeyframePolicy.cs`, new).**
- The policy is `ShouldForce(ptsMs, lastKeyPtsMs) = ptsMs − lastKeyPtsMs ≥ 2000`.
- The encode thread computes each frame's stream time from the clock (`clock.ToMs(clock.TickQpc(n))`) and forces `AV_PICTURE_TYPE_I` when the policy says so. The forced-IDR encoder options are unchanged.
- `_lastKeyMs` is also advanced by keyframes the encoder makes itself (from each output packet's KEY flag), so its own GOP and scene cuts count.
- The encoder's `gop_size` = 2×fps stays as a backstop.
- `ReopenVideo` now reads `Stopwatch.GetTimestamp()` **after** the new encoder is open, resets `_lastKeyMs`, and no longer takes a stale `now` parameter. This applies to re-init, SetBitrate re-open, and the live-reject fallback.

**SendQueue.**
- Drops start when the total span exceeds 3 s. Oldest GOP tails are then dropped until the **droppable video backlog** (newest video DTS − oldest queued delta frame) is ≤ 2 s. This is the hysteresis; audio and keyframes are left out of that measure, so they can no longer pin it above the limit.
- Keyframe requests are limited to one per 2 s of packet time.
- New 15 s span ceiling: `Enqueue` returns false, and the session logs it and raises `Failed` with "The connection to the platform was lost." followed by teardown.

**Minors.**
1. `DisposeCore` cancels a pending re-init (`TrySetCanceled`).
2. A failed re-init faults its task with `StreamSessionException(EncoderFailed)`, and `Failed` is raised. `RaiseFailed` now faults a pending re-init instead of completing it successfully.
3. Encoder options FFmpeg did not consume are logged to ffmpeg.log as `"<codec>: options NOT used: …"` or `"<codec>: all options accepted"` and exposed as `VideoEncoder.UnusedOptionNames`. A new test asserts the list is empty.
4. New `UrlMasker.SecretParts`: the whole key part, the key before `?`, and every query value, each also URL-decoded, are registered as secrets. Values shorter than 4 characters (e.g. `s_bl=1`) are deliberately skipped, because redacting "1" would mangle every log line.
5. `_stopEvent` and `_frameEvent` are disposed at the end of `DisposeCore`. A late `Set` in `RaiseFailed` is guarded, and `PushVideo`'s `Set` is already inside try/catch.

The `IStreamSession` docs note that `ReinitAsync` faults or is cancelled. `DroppedFramesTotal` was already documented as "send-queue drops plus ticks skipped because encoding fell behind".

## Encoder options NOT consumed on this PC

New test `Encoder_accepts_every_option_we_set`; every option was consumed by all three encoders available here:
```
h264_qsv: unused options = []      (preset, forced_idr, look_ahead, async_depth)
libopenh264: unused options = []   (rc_mode, allow_skip_frames)
h264_mf: unused options = []       (rate_control, scenario, hw_encoding)
```

## RED → GREEN

RED: with stubs for `KeyframePolicy` and `SecretParts`, and the old queue logic behind the new constructor, `dotnet test --filter SendQueue|KeyframePolicy|UrlSecrets` gave `Failed: 11, Passed: 6`. The old queue reproduced the storm:
```
Failed SendQueueTests.Never_drained_steady_state_requests_keyframes_at_most_every_2s_and_never_drops_audio(gop: 60)
   requests at 3033 and 3100 ms
Failed SendQueueTests.Never_drained_queue_trips_the_ceiling_deterministically
   Expected: 15015  Actual: -1
```
GREEN: the pure test set (SendQueue, KeyframePolicy, UrlSecrets, UrlMasker, ErrorTexts, Timeline, FfmpegLocator) gave `Passed! Failed: 0, Passed: 42`.

## New and changed tests

**SendQueue.**
- Steady state with a writer that never drains, 30 fps video plus audio every 21 ms, two cases: keyframes only on request, and a 60-frame GOP. It asserts:
  - (i) keyframe requests are ≥ 2000 ms apart;
  - (ii) no audio is ever dropped, and no keyframes;
  - video is shed;
  - at most 9 keyframes in 14 s.
- (iii) The ceiling trips at exactly dts 15015 (the first packet whose span exceeds 15 s).
- (iv) Recovery: after a tail drop with no keyframe following, the queue drains, the next delta frame is dropped, and video resumes at the keyframe.
- Existing tests were adjusted to the low-water semantics.

**KeyframePolicy.** 6 cases: 1966 → no, 2000 → yes, 2333 (skipped ticks) → yes, encoder-made keyframe restarts the 2 s window, no keyframe yet → yes.

**UrlSecrets.** The Facebook-style key `FB-123456789-0-AbCdEf?s_bl=1&a=SECRET%2BVALUE` registers the whole key, `FB-123456789-0-AbCdEf`, `SECRET%2BVALUE` and the decoded `SECRET+VALUE`, but not `1`. A URL without a key has no secrets.

**Integration (b).** Every keyframe interval must be ≤ 2.2 s across the change; this also covers the libopenh264 re-open path.

**Integration (e).** Every keyframe interval must be ≤ 2.2 s for the reader that spans the switch (at least 2 intervals required) and for the reader started after it.

## Command and output

`dotnet test tests/EasyLive.Core.Tests --logger "console;verbosity=detailed"`. The fixture starts and stops MediaMTX itself, and no mediamtx process was left running afterwards.
```
Total tests: 171
     Passed: 167
    Skipped: 4          (the existing manual tests)
```

### (e) ReinitAsync(480p30) on QuickSync, after the fix

The intervals after the switch are now exact 2 s and nothing is skipped:
```
reinit took 395 ms; reopens=1 connects=1 health=StreamHealth { SendQueueMs = 0, DroppedFramesTotal = 0, EncodeLagMs = 0, CurrentKbps = 1000 }
spanning reader decoded sizes: 1280x720,854x480 | video h264 High 854x480 fps=28.32 maxGap=0.433s keyIntervals=[0.43,2.00,2.00,2.00] | audio maxGap=0.022s
new reader: video h264 High 854x480 fps=30.00 maxGap=0.034s keyIntervals=[2.00]
```
Before: `keyIntervals=[0.07,2.30,2.00,2.00]`, `DroppedFramesTotal=10`. The 0.43 s video gap is the QSV open time, which still meets the ≤ 1 s requirement. In an earlier run of the same fix it was 0.60 s with a 563 ms open; the open time varies.

### (b) SetBitrate(1000)

QSV, live:
```
CurrentKbps=1000 after 31 ms; reopens=0 connects=1; kbps before=1676 after=1016; maxGap=0.034s keyIntervals=[2.00,2.00,2.00,2.00]
```
libopenh264, re-open:
```
CurrentKbps=1000 after 31 ms; reopens=1 connects=1; kbps before=2129 after=992; maxGap=0.067s
keyIntervals=[0.73,0.73,0.67,0.70,0.37,0.57,...] (all ≤ 2.2 s)
```

### (a) 10 s with a 3 s freeze

QSV: `video h264 High 1280x720 fps=30.00 maxGap=0.034s keyIntervals=[2.00,2.00,2.00]`, AAC 48 kHz, 2 channels.

libopenh264: High 1280x720, 30.00 fps, all intervals ≤ 2.2 s.

### (c), (d), key-rejected, no-frames

All pass. Messages are unchanged: "Could not connect — check your internet connection." after 5.5 s; "The platform did not accept your stream key — paste a new key."; "Nothing to stream — the screen did not provide any picture." after 5.0 s.

## Remaining concerns

1. libopenh264 still adds extra IDRs on moving content. Intervals are now guaranteed ≤ 2 s, but they are not regular 2 s.
2. FFmpeg 8.1 instead of 7.x.
3. AMF and NVENC are unverified (no such GPUs here).
4. After a live drop, QSV keeps the VBV at 2 × the start bitrate.
5. The QSV re-open gap is about 0.4–0.6 s.
6. While waiting for a keyframe under the 2 s request limit, heavy congestion can freeze the picture for up to 2 s. That is the intended trade-off against a keyframe storm.
