using EasyLive.Core.Audio;
using Microsoft.Extensions.Time.Testing;

namespace EasyLive.Core.Tests;

public class AudioMixerTests
{
    private sealed class FakeInput : IAudioInput
    {
        public event Action<AudioChunk>? Data;
        public event Action? Lost;
        public bool Started { get; private set; }
        public void Start() => Started = true;
        public void Push(float[] s, long qpc = 0) => Data?.Invoke(new AudioChunk(s, qpc));
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

    [Fact]
    public void Idle_system_for_20ms_emits_10ms_silence_plus_mic()
    {
        var time = new FakeTimeProvider();
        var sys = new FakeInput(); var mic = new FakeInput();
        var (m, got, _) = Make(sys, mic, time);
        mic.Push(Const(0.25f, 2000));
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Empty(got);
        time.Advance(TimeSpan.FromMilliseconds(10));
        var c = Assert.Single(got);
        Assert.Equal(960, c.Samples.Length);         // 10 ms stereo @ 48 kHz
        Assert.All(c.Samples, s => Assert.Equal(0.25f, s, 4));
        time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(2, got.Count);
        m.Dispose();
    }

    [Fact]
    public void Fallback_is_silent_when_no_mic_and_stops_while_system_flows()
    {
        var time = new FakeTimeProvider();
        var sys = new FakeInput();
        var (m, got, _) = Make(sys, null, time);
        time.Advance(TimeSpan.FromMilliseconds(10)); time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.All(got.Single().Samples, s => Assert.Equal(0f, s));
        sys.Push(Const(0.3f)); time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Equal(2, got.Count);                  // fresh system chunk suppresses fallback
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
        time.Advance(TimeSpan.FromMilliseconds(10)); time.Advance(TimeSpan.FromMilliseconds(10));
        Assert.All(got.Single().Samples, s => Assert.Equal(0.4f, s, 4));
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
