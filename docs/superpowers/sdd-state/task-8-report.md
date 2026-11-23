# Task 8 report: AudioMixer

Commit: 1fa9ea4 feat: audio mixer

## What was done
- Added NAudio 2.2.1 to EasyLive.Core.
- src/EasyLive.Core/Audio/: IAudioInput.cs (AudioChunk, IAudioInput), AudioMixer.cs, CaptureAudioInput.cs (shared NAudio conversion to 48 kHz stereo float32: BufferedWaveProvider -> WdlResampling -> mono->stereo / first-two-channels), LoopbackAudioInput.cs, MicrophoneAudioInput.cs (TryCreate returns null when no capture device).
- AudioMixer: Start()/Dispose(), TimeProvider injectable, driven by system chunks, mic queue padded with silence, 200 ms backlog bound (stereo-pair aligned), mute flags, clamp, QpcTicks = system chunk's. Fallback 10 ms timer: when no system chunk for 20 ms emits 10 ms silence+mic; it catches up on late timer ticks (Windows timer granularity ~15 ms) so the clock stays real-time. Exact Notice texts as specified; each notice fires once.
- Tests: tests/EasyLive.Core.Tests/AudioMixerTests.cs (13 tests, fakes only) + AudioInputsManualTests.cs (Skip="manual").

## RED / GREEN
- RED: `dotnet test tests/EasyLive.Core.Tests` before implementation -> CS0234/CS0246 (EasyLive.Core.Audio, AudioMixer, IAudioInput, AudioChunk missing).
- First GREEN run had 2 failures: an integer-math bug in the idle threshold (float rounding) was fixed; FakeTimeProvider.Advance(20ms) fires both timer ticks at the final timestamp so tests advance in 10 ms steps.
- Final: `Passed! Failed: 0, Passed: 105, Skipped: 4, Total: 109` (full Core test project, no warnings).

## Manual smoke (run once, un-skipped, skip restored before commit)
```
mic: present
MicActive=True chunks=318 samples=305280 (~3.18s) peak=0.0000 oddLength=False
```
Mic exists on this PC, so the null-mic path was NOT exercised on real hardware (covered by the unit test). Peak 0.0 because nothing was playing and the mic was silent (loopback idle -> the fallback timer supplied the continuous clock: ~3.18 s of audio in 3 s run, which verifies the idle-clock behaviour). Real non-silent resampling levels were not verified.

## Self-review
- Locks: _emitLock serialises Mixed (ordered, timer vs system thread); _micLock guards only the queue so the mic capture thread never waits on Mixed handlers. Lock order emit -> mic only.
- NAudio callbacks (DataAvailable, RecordingStopped) and the timer callback wrap handlers in try/catch.
- Dispose unsubscribes, stops timer, disposes inputs (and stops/disposes the capture devices).

## Concerns
- Mixed handlers run under _emitLock; a slow subscriber delays the next chunk (and a subscriber that calls back into mixer synchronously is fine, but one that blocks on another thread needing the lock could deadlock).
- `Lost` raised only for RecordingStopped with an exception; a default-device change without an error is not detected.
- Mic notice for null mic is raised in Start() (subscribe to Notice before Start).
- Mic path uses Queue<float> (per-sample enqueue), fine for 96k samples/s.
- Line-ending warnings (LF->CRLF) from git only.

## Fix round 1
Changes:
- Important 1/3: AudioMixer keeps one running QPC timeline (`_timelineEnd`). Fallback starts only when wall clock leads it by more than `FallbackThreshold` (120 ms), then fills in 10 ms pieces until caught up (stays active until a system chunk arrives). CaptureAudioInput stamps chunks contiguously (re-sync to arrival-duration only if off by >50 ms, `AudioTiming.ResyncTolerance`). Incoming system chunks: within 50 ms of the timeline -> re-stamped to it; earlier by more -> overlapping leading frames dropped; later -> kept (real capture gap). Output is monotonic and gap-free. Integer QPC conversion for fake clocks.
- Important 2: system Start failure detaches handlers and rethrows (mic not started); mic Start failure -> MicActive=false, one "Microphone not found..." notice (documented choice), mixer continues.
- Minor: ChannelDownmix.ToStereo (WASAPI order, centre/rears/sides at 0.707, LFE ignored, normalised so no clipping); used for non-stereo capture formats.
Tests: AudioMixerTests (idle rule 120 ms, bursty 5x10 ms every 50 ms never triggers fallback, system->idle->system strictly increasing & contiguous, overlap clipping, mic start throws, system start throws) + ChannelDownmixTests (6).
Command: `dotnet test tests/EasyLive.Core.Tests` -> Passed! Failed: 0, Passed: 116, Skipped: 4, Total: 120.
Manual smoke (un-skipped temporarily, system beeps played concurrently):
`MicActive=True chunks=251 samples=318720 (~3.32s audio, 3.30s wall since before Start) peak=0.4084` -> audio duration matches wall clock.
