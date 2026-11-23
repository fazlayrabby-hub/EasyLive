# SDD ledger — plan: D:/MT_Projects/EasyLive/docs/superpowers/plans/2026-11-23-easylive.md

Repo: D:/MT_Projects/EasyLive  Branch: feature/easylive-v1  Base commit (docs): 9a73399
Spec: docs/superpowers/specs/2026-11-23-easylive-design.md (reachable)
Env: .NET 8 SDK installed by winget (user approved). Bash needs `export PATH="$PATH:/c/Program Files/dotnet"`.
Git identity for commits: -c user.name="EasyLive Dev" -c user.email="dev@easylive.local"

## Pre-flight scan
| Pair/Task | Produces vs consumes | Finding |
|---|---|---|
| T1→T2,T3,T9,T10 | QualityRung/StreamPlan/HardwareProfile records | consistent |
| T1 own | tests vs code | numbers check (6000/3000/1560/600 budgets); "OpenH264 cap" ambiguous |
| T2 own | Decide() vs tests | consistent |
| T3→T10 | IAdapterSource, HardwareDetector.Detect | consistent |
| T4→T10 | IProbeTransport, OfflineException | consistent; "budget respected" test vague |
| T5→T10,T11 | KeyStore, KeyValidator, Platforms | consistent |
| T6→T10 | BlankFrameDetector.Observe | consistent |
| T7→T9,T10 | CaptureFrame only described in prose | GAP |
| T8→T10 | AudioMixer/IAudioInput | consistent |
| T9→T10 | IStreamSession | test (a) needs synthetic GPU frames, undefined |
| T10→T11 | SessionOrchestrator/SessionStatus | consistent |
| T12 | packaging | no conflicts |

## Rulings
- Ruling: define `public sealed record CaptureFrame(nint Texture, int Width, int Height, long QpcTicks);` in Task 7 (Capture/IFrameSource.cs); Texture = native ID3D11Texture2D pointer — plan prose omitted the exact type — T9/T10 need it — cost if wrong: small refactor.
- Ruling: software-only (OpenH264) allowed rungs are exactly {720p30, 480p30} — plan's "above 720p30" is ambiguous (720p60) — spec says capped 720p30 — cost if wrong: one more rung option.
- Ruling: T1 loss rule applies after the OpenH264 cap and never goes below 480p30.
- Ruling: T4 "budget" test = fake transport that never completes; RunAsync must return within 8 s (use fake clock/cancellation) with upload computed from bytes so far.
- Ruling: T9 implementer creates a SyntheticFrameSource test helper (D3D11 texture with moving gradient) for integration tests.

## Progress
Task 1: complete (commits 9a73399..051145b, review clean; 5 Minor parked: OpenH264 filter by name string, extra boundary tests, Ladder mutability, input guards, redundant using)
Pending user action: gh installed; user must run `gh auth login`; then create PUBLIC repo fazlayrabby-hub/EasyLive and push feature/easylive-v1 (needs user confirmation of name + docs public). gh path: C:\Program Files\GitHub CLI\gh.exe
Task 2: complete (commits 051145b..e183dcd, review clean; Minor parked: congestion-branch/boundary/streak-reset tests, streak unbounded at Max)
GitHub: public repo fazlayrabby-hub/EasyLive created, master + feature/easylive-v1 pushed (as of 051145b). Push again at milestones.
Task 3: complete (commits e183dcd..e44dcd4, review clean; Minor parked: AMD iGPU vs NVIDIA ranking when capturing unknown, possible CS8602, swallowed DXGI exception no trace, Detect forwarding test, VendorId matching)
Task 4: complete (commits e44dcd4..aab5acf, review clean; real smoke ran OK 92.8 Mbps so Cloudflare accepts chunked POST; Minor parked: swallowed upload exception no log, ct check after catch, no per-connect timeout (hang -> false Offline), upload-cancelled-before-bytes masks Offline, DNS in connect time, missing budget-expiry-in-connect tests)
Task 5: review needs fixes -> fix round 1/5 dispatched (Important: IOException swallowed in ReadEntries can wipe other platform key; server URL accepted as key). Fix base for re-review: e33b03f. Minors folded into fix: zero-width chars, quotes, atomic write, null JSON value. Minor deferred: thread safety, Save input guard.
Task 5: fix round 1/5 (2 addressed, 0 open; commits e33b03f..a7a5376)
Task 5: complete (commits aab5acf..a7a5376, review clean after 1 fix round)
Task 5: minor (deferred): stale .tmp on failed write, corrupt store file treated as empty drops other entry, tests use literal invisible chars (use \u escapes), no thread safety, Save has no input guard
Task 6: complete (commits a7a5376..8ac02b5, review clean; Minor parked: weak Alpha_is_ignored test, no gray-8 boundary test, comment "luma ~29" should be 28, no partial-pixel test)
Ruling (T7 concern): WGC only emits frames when content changes -> Task 9 (FfmpegStreamSession) MUST repeat the last frame to hold the plan's fps on static content (CFR output). Carry into Task 9 dispatch. Also: CaptureTargets.List() replaces IFrameSource.ListTargets (static iface member) — Task 11 must call CaptureTargets.List().
Task 7: implemented 5f40212 (DONE_WITH_CONCERNS: border-off unverified visually; window-target + stall timer wiring not run manually) -> in review
Task 7: review needs fixes -> fix round 1/5 dispatched (Important: exceptions escaping callbacks/timer; Stop deadlock under _gate; Width/Height vs texture size mismatch on resize; border-off IGraphicsCaptureSession3 vtable wrong/silent). Fix base for re-review: 5f40212. Minors deferred: Timer.Dispose doesn't wait, message says "window" for monitor, shell windows in list, device not exposed, FakeFrameSource test low value, stall threshold not injectable.
Task 7: fix round 1/5 (4 addressed, 0 open; commits 5f40212..1cf237c)
Task 7: complete (commits 8ac02b5..1cf237c, review clean after 1 fix round)
Task 7: minor (deferred): Start() called twice tears down the running session (guards inside try -> Stop()), BorderStatus only via Debug.WriteLine, border-off visually unverified, window-target capture + stall timer wiring not run manually, shell windows in list, Timer.Dispose doesn't wait
Task 8: implemented 1fa9ea4 -> review needs fixes -> fix round 1/5 dispatched (Important: fallback 20ms threshold misfires vs ~50ms NAudio burst cadence + non-contiguous timestamps; mic Start() failure aborts mixer start (Review Focus 1); fallback clock anchor gap/overlap). Fix base for re-review: 1fa9ea4. Folded minor: 5.1 downmix helper. Deferred minors: Mixed after Dispose, duplicate-emission race, Dispose-from-handler self-join, resampler latency, handler exceptions swallowed, loose backlog test, volatile mute flags, Lost only on exception.
Ruling carried into Task 9: IStreamSession.PushAudio/PushVideo MUST be non-blocking (enqueue only) because AudioMixer.Mixed handlers run under the emit lock. Audio chunks arrive timestamp-contiguous (after T8 fix); video frames are only emitted on screen change (T7) so T9 repeats the last frame for CFR.
Task 8: fix round 1/5 (3 addressed + downmix, 0 open; commits 1fa9ea4..141d1aa)
Task 8: complete (commits 1cf237c..141d1aa, review clean after 1 fix round)
Task 8: minor (deferred): 5.0 layout mapping wrong, DownmixProvider allocs per read, mic-start-failure notice reuses NoMicMessage, no 7.1/4ch downmix tests, Mixed handlers run under emit lock, Mixed after Dispose, Dispose from handler self-join, Lost only on exception
Ruling (T9): spec says frames stay on GPU "where possible" -> v1 path = copy capture texture to a D3D11 staging texture, map, libswscale BGRA->NV12 (+downscale to rung size) on CPU, hardware encoder fed from system memory frames. Measure CPU; GPU-resident path is a later optimisation. Cost if wrong: higher CPU on weak PCs.
Task 9: implemented 141d1aa..5e6de42 (DONE_WITH_CONCERNS; opus). 153 passed/4 skipped; QSV integration a-e pass vs local MediaMTX; CPU ~22% of a core at 720p30. FFmpeg n8.1 LGPLv3 + FFmpeg.AutoGen 8.1.0 (BtbN latest has no 7.x). libopenh264 present & used for fallback (extra keyframes ~0.6-0.8s; may ignore bitrate cap ~9 Mbps on extreme content; h264_mf alternative = one-line change in VideoEncoder.CodecNameFor). AMF/NVENC unverified (AMD PC). Reinit on QSV ~0.35 s blip. RTMPS real platform untested.
Ruling (T9 concern): keep libopenh264 as spec'd (Facebook only requires keyframe interval <= 2 s; more frequent keyframes are allowed); the bitrate-cap overshoot on extreme content is a software-fallback limitation -> carry to final review/Task 12 validation; revisit h264_mf if Facebook rejects. -> in review
Task 9: review needs fixes -> fix round 1/5 dispatched to the implementer (Important: 2 s keyframe interval broken after re-init/re-open/skipped ticks (GOP counted in frames; stale pts after open; test e showed 2.30 s) -> time-based forced IDR; send-queue congestion -> IDR every other frame + no memory ceiling -> rate-limit key requests, video-only span, hard ceiling = lost connection). Fix base for re-review: 5e6de42. Minors folded: pending reinit completion on Dispose, ReinitAsync faults on failure, log unconsumed encoder opts, mask key parts/query values, dispose wait handles. Deferred minors: connect not cancellable on Stop (12 s), sync log writer, Map flags, DroppedFramesTotal semantics doc, fixed delays in tests.
Task 9: fix round 1/5 (2 addressed + 5 minors, 0 open; commits 5e6de42..a614d47)
Task 9: complete (commits 141d1aa..a614d47, review clean after 1 fix round)
Task 9: minor (deferred): short (<4 char) whole stream key not redacted in logs (apply min length only to query values); ReinitAsync checks _stopping outside _gate (hang during shutdown); ReinitAsync doc vs behaviour on stopped session; extra short keyframe intervals on h264_mf/libopenh264 after skipped ticks; ceiling checked before shedding; misplaced doc comment in UrlMasker; connect not cancellable on Stop (12 s); sync log writer; Map flags; fixed delays in tests.
Next: Task 10 (SessionOrchestrator), Task 11 (WPF app), Task 12 (validation/packaging/licences). Handoff to AMD PC after Task 9.
