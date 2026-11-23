using System.Diagnostics;
using EasyLive.Core.Audio;
using Microsoft.Extensions.Time.Testing;

namespace EasyLive.Core.Tests;

public class AudioMixerTests
{
    private sealed class FakeInput : IAudioInput
    {
        private long _next;
        public event Action<AudioChunk>? Data;
        public event Action? Lost;
        public bool Started { get; private set; }
        public bool StartThrows { get; set; }
        public void Start() { if (StartThrows) throw new InvalidOperationException("start failed"); Started = true; }
        /// <summary>Without an explicit timestamp, chunks are contiguous like a real input.</summary>
        public void Push(float[] s, long? qpc = null)
        {
            long q = qpc ?? _next;
            _next = q + (long)Math.Round(s.Length / 2 * (double)Stopwatch.Frequency / 48000);
            Data?.Invoke(new AudioChunk(s, q));
        }
        public void Lose() => Lost?.Invoke();
    }

    private static float[] Const(float v, int n = 960) => Enumerable.Repeat(v, n).ToArray();

    private static (AudioMixer mixer, List<AudioChunk> got, List<string> notices) Make(
        FakeInput sys, FakeInput? mic, FakeTimeProvider? time = null)
    {
        var m = new AudioMixer(sys, mic, time);
        var got = new List<AudioChunk>();
        var notices = new List<string>();
        m.Mixed += got.Add;
        m.Notice += notices.Add;
        m.Start();
        return (m, got, notices);
    }

    [Fact]
    public void Sums_system_and_mic()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic);
        mic.Push(Const(0.3f)); sys.Push(Const(0.3f));
        Assert.All(got.Single().Samples, s => Assert.Equal(0.6f, s, 4));
        m.Dispose();
    }

    [Fact]
    public void Clips_to_unit_range()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic);
        mic.Push(Const(0.8f)); sys.Push(Const(0.7f));
        mic.Push(Const(-0.8f)); sys.Push(Const(-0.7f));
        Assert.All(got[0].Samples, s => Assert.Equal(1f, s));
        Assert.All(got[1].Samples, s => Assert.Equal(-1f, s));
        m.Dispose();
    }

    [Fact]
    public void MuteMic_passes_only_system()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic);
        m.MuteMic = true;
        mic.Push(Const(0.3f)); sys.Push(Const(0.3f));
        Assert.All(got.Single().Samples, s => Assert.Equal(0.3f, s, 4));
        m.Dispose();
    }

    [Fact]
    public void MuteSystem_passes_only_mic()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic);
        m.MuteSystem = true;
        mic.Push(Const(0.2f)); sys.Push(Const(0.5f));
        Assert.All(got.Single().Samples, s => Assert.Equal(0.2f, s, 4));
        m.Dispose();
    }

    [Fact]
    public void Null_mic_notices_once_and_still_mixes()
    {
        var sys = new FakeInput();
        var (m, got, notices) = Make(sys, null);
        sys.Push(Const(0.3f)); sys.Push(Const(0.3f));
        Assert.False(m.MicActive);
        Assert.Equal(new[] { "Microphone not found — streaming game sound only." }, notices);
        Assert.Equal(2, got.Count);
        Assert.All(got[0].Samples, s => Assert.Equal(0.3f, s, 4));
        m.Dispose();
    }

    [Fact]
    public void Mic_lost_mid_run_notices_and_mixing_continues()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, notices) = Make(sys, mic);
        Assert.True(m.MicActive);
        mic.Lose();
        mic.Push(Const(0.5f));                       // ignored after loss
        sys.Push(Const(0.3f));
        Assert.False(m.MicActive);
        Assert.Equal(new[] { "Microphone disconnected — streaming game sound only." }, notices);
        Assert.All(got.Single().Samples, s => Assert.Equal(0.3f, s, 4));
        m.Dispose();
    }

    [Fact]
    public void Output_QpcTicks_is_the_system_chunks()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic);
        mic.Push(Const(0.1f), 999);
        sys.Push(Const(0.1f), 123456789);
        Assert.Equal(123456789, got.Single().QpcTicks);
        m.Dispose();
    }

    [Fact]
    public void Short_mic_is_padded_with_silence()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic);
        mic.Push(Const(0.4f, 100));
        sys.Push(Const(0.1f, 960));
        var s = got.Single().Samples;
        Assert.Equal(960, s.Length);
        Assert.Equal(0.5f, s[99], 4);
        Assert.Equal(0.1f, s[100], 4);
        Assert.Equal(0.1f, s[959], 4);
        m.Dispose();
    }

    [Fact]
    public void Mic_backlog_over_200ms_drops_oldest()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic);
        mic.Push(Const(0.9f, 48000));                // 250 ms of old audio
        mic.Push(Const(0.9f, 48000));                // total 500 ms (96000 samples)
        mic.Push(Const(0.2f, 960));                  // newest 10 ms
        sys.Push(Const(0f, 960));
        Assert.Equal(0.9f, got.Single().Samples[0], 4);
        // drain: only ~200 ms (19200 samples) may remain beyond the chunk just taken
        int extra = 0;
        while (true)
        {
            sys.Push(Const(0f, 960));
            if (got[^1].Samples[0] == 0f) break;
            extra++;
            Assert.True(extra < 100);
        }
        Assert.InRange(extra, 1, 19200 / 960 + 1);
        m.Dispose();
    }

    private static void Run(FakeTimeProvider t, int ms)
    {
        for (int i = 0; i < ms / 10; i++) t.Advance(TimeSpan.FromMilliseconds(10));   // fire the 10 ms timer one tick at a time
    }

    private static long Qpc(FakeTimeProvider t) =>
        t.GetTimestamp();   // fake frequency equals Stopwatch.Frequency on Windows (10 MHz)

    private static long Dur(AudioChunk c) => (long)Math.Round(c.Samples.Length / 2 * (double)Stopwatch.Frequency / 48000);

    private static void AssertContiguous(List<AudioChunk> got)
    {
        for (int i = 1; i < got.Count; i++)
            Assert.InRange(got[i].QpcTicks - (got[i - 1].QpcTicks + Dur(got[i - 1])), -1, 1);
    }

    [Fact]
    public void Idle_system_emits_nothing_until_over_120ms_then_fills_in_10ms_pieces_with_mic()
    {
        var time = new FakeTimeProvider();
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic, time);
        mic.Push(Const(0.25f, 2000));
        Run(time, 120);
        Assert.Empty(got);                           // 120 ms is not "more than" the threshold
        Run(time, 10);
        Assert.Equal(13, got.Count);                 // timeline filled up to now in 10 ms pieces
        Assert.All(got, c => Assert.Equal(960, c.Samples.Length));
        Assert.All(got[0].Samples, s => Assert.Equal(0.25f, s, 4));
        Run(time, 10);
        Assert.Equal(14, got.Count);
        AssertContiguous(got);
        m.Dispose();
    }

    [Fact]
    public void Bursty_system_capture_never_triggers_fallback()
    {
        var time = new FakeTimeProvider();
        var sys = new FakeInput();
        var (m, got, _) = Make(sys, null, time);
        for (int burst = 0; burst < 20; burst++)
        {
            long start = Qpc(time);
            Run(time, 50);                           // WASAPI polls ~50 ms, then delivers 5 packets at once
            for (int k = 0; k < 5; k++) sys.Push(Const(0.1f), start + k * (Stopwatch.Frequency / 100));
        }
        Assert.Equal(100, got.Count);                // exactly the system chunks, no injected silence
        Assert.All(got, c => Assert.Equal(0.1f, c.Samples[0], 4));
        AssertContiguous(got);
        m.Dispose();
    }

    [Fact]
    public void System_idle_system_timeline_is_strictly_increasing_and_gap_free()
    {
        var time = new FakeTimeProvider();
        var sys = new FakeInput();
        var (m, got, _) = Make(sys, null, time);
        long dur = Stopwatch.Frequency / 100;
        for (int k = 0; k < 3; k++) { sys.Push(Const(0.2f), Qpc(time)); Run(time, 10); }
        Run(time, 300);                              // idle: fallback fills
        sys.Push(Const(0.2f), Qpc(time) - dur);      // a real input stamps arrival minus duration
        Run(time, 10);
        sys.Push(Const(0.2f), Qpc(time) - dur);
        Assert.True(got.Count > 20);
        for (int i = 1; i < got.Count; i++) Assert.True(got[i].QpcTicks > got[i - 1].QpcTicks);
        AssertContiguous(got);
        m.Dispose();
    }

    [Fact]
    public void Resumed_system_chunk_overlapping_the_timeline_is_clipped()
    {
        var sys = new FakeInput();
        var (m, got, _) = Make(sys, null);
        long f = Stopwatch.Frequency;
        sys.Push(Const(0.1f, 9600), 1_000_000_000);                 // 100 ms
        long end = 1_000_000_000 + f / 10;
        sys.Push(Const(0.2f, 9600), end - f * 6 / 100);             // starts 60 ms early (beyond 50 ms tolerance)
        Assert.Equal(2, got.Count);
        Assert.Equal(end, got[1].QpcTicks);
        Assert.Equal(9600 - 2 * 2880, got[1].Samples.Length);       // 60 ms of frames dropped
        AssertContiguous(got);
        m.Dispose();
    }

    [Fact]
    public void Fallback_is_silent_when_no_mic()
    {
        var time = new FakeTimeProvider();
        var sys = new FakeInput();
        var (m, got, _) = Make(sys, null, time);
        Run(time, 130);
        Assert.NotEmpty(got);
        Assert.All(got, c => Assert.All(c.Samples, s => Assert.Equal(0f, s)));
        m.Dispose();
    }

    [Fact]
    public void System_lost_notices_and_fallback_keeps_going()
    {
        var time = new FakeTimeProvider();
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, notices) = Make(sys, mic, time);
        sys.Lose();
        Assert.Equal(new[] { "Game sound capture stopped." }, notices);
        mic.Push(Const(0.4f, 960));
        Run(time, 130);
        Assert.All(got[0].Samples, s => Assert.Equal(0.4f, s, 4));
        m.Dispose();
    }

    [Fact]
    public void Mic_start_failure_still_starts_mixer_with_one_notice()
    {
        var sys = new FakeInput(); var mic = new FakeInput { StartThrows = true };
        var (m, got, notices) = Make(sys, mic);
        sys.Push(Const(0.3f));
        Assert.False(m.MicActive);
        Assert.Equal(new[] { "Microphone not found — streaming game sound only." }, notices);
        Assert.Single(got);
        mic.Lose();                                  // late Lost must not raise a second notice
        Assert.Single(notices);
        m.Dispose();
    }

    [Fact]
    public void System_start_failure_propagates_and_detaches()
    {
        var sys = new FakeInput { StartThrows = true }; var mic = new FakeInput();
        var m = new AudioMixer(sys, mic);
        var got = new List<AudioChunk>(); m.Mixed += got.Add;
        Assert.Throws<InvalidOperationException>(m.Start);
        Assert.False(mic.Started);                   // mic never started
        sys.Push(Const(0.3f));
        Assert.Empty(got);
        m.Dispose();
    }

    [Fact]
    public void Start_starts_inputs_and_Dispose_stops_events()
    {
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic);
        Assert.True(sys.Started && mic.Started);
        m.Dispose();
        sys.Push(Const(0.3f));
        Assert.Empty(got);
    }
}
