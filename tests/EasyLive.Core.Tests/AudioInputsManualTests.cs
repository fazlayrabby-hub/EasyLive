using EasyLive.Core.Audio;
using Xunit.Abstractions;

namespace EasyLive.Core.Tests;

public class AudioInputsManualTests
{
    private readonly ITestOutputHelper _out;
    public AudioInputsManualTests(ITestOutputHelper o) => _out = o;

    [Fact(Skip = "manual: needs audio devices")]
    public async Task Mixes_real_loopback_and_mic_for_3s()
    {
        var mic = MicrophoneAudioInput.TryCreate();
        _out.WriteLine(mic is null ? "mic: none (TryCreate returned null)" : "mic: present");
        using var mixer = new AudioMixer(new LoopbackAudioInput(), mic);
        int chunks = 0, samples = 0; float peak = 0; bool badFormat = false;
        mixer.Notice += n => _out.WriteLine("notice: " + n);
        mixer.Mixed += c =>
        {
            Interlocked.Increment(ref chunks);
            Interlocked.Add(ref samples, c.Samples.Length);
            if (c.Samples.Length % 2 != 0) badFormat = true;
            foreach (var s in c.Samples) peak = Math.Max(peak, Math.Abs(s));
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        mixer.Start();
        await Task.Delay(3000);
        _out.WriteLine($"MicActive={mixer.MicActive} chunks={chunks} samples={samples} (~{samples / 2 / 48000.0:F2}s audio, {sw.Elapsed.TotalSeconds:F2}s wall since before Start) peak={peak:F4} oddLength={badFormat}");
        Assert.True(chunks > 0);
    }
}
