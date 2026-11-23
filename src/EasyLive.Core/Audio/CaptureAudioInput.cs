using System.Diagnostics;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace EasyLive.Core.Audio;

/// <summary>
/// Wraps a NAudio capture device and converts whatever the device mix format is to 48 kHz stereo float32.
/// </summary>
public abstract class CaptureAudioInput : IAudioInput, IDisposable
{
    private const int OutRate = 48000;
    private readonly IWaveIn _capture;
    private readonly BufferedWaveProvider _buffer;
    private readonly ISampleProvider _stereo48k;
    private readonly float[] _readBuf = new float[OutRate / 10 * 2];     // up to 100 ms per read
    private int _stopped;

    public event Action<AudioChunk>? Data;
    public event Action? Lost;

    protected CaptureAudioInput(IWaveIn capture)
    {
        _capture = capture;
        _buffer = new BufferedWaveProvider(capture.WaveFormat)
        {
            DiscardOnBufferOverflow = true,
            BufferDuration = TimeSpan.FromSeconds(2),
            ReadFully = false,
        };
        ISampleProvider p = _buffer.ToSampleProvider();
        if (p.WaveFormat.SampleRate != OutRate) p = new WdlResamplingSampleProvider(p, OutRate);
        if (p.WaveFormat.Channels == 1) p = new MonoToStereoSampleProvider(p);
        else if (p.WaveFormat.Channels > 2) p = new FirstTwoChannelsProvider(p);
        _stereo48k = p;
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += OnStopped;
    }

    public void Start() => _capture.StartRecording();

    private void OnData(object? sender, WaveInEventArgs e)
    {
        try
        {
            long arrival = Stopwatch.GetTimestamp();
            _buffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
            var chunks = new List<float[]>();
            int total = 0, n;
            while ((n = _stereo48k.Read(_readBuf, 0, _readBuf.Length)) > 0)
            {
                chunks.Add(_readBuf.AsSpan(0, n).ToArray());
                total += n;
            }
            if (total == 0) return;
            // QPC at the first sample = arrival minus what we are about to hand over.
            long ticks = arrival - (long)(total / 2 / (double)OutRate * Stopwatch.Frequency);
            foreach (var c in chunks)
            {
                Data?.Invoke(new AudioChunk(c, ticks));
                ticks += (long)(c.Length / 2 / (double)OutRate * Stopwatch.Frequency);
            }
        }
        catch { /* never let an exception escape a NAudio callback thread */ }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is null || Interlocked.Exchange(ref _stopped, 1) == 1) return;
        try { Lost?.Invoke(); } catch { }
    }

    public void Dispose()
    {
        _capture.DataAvailable -= OnData;
        try { _capture.StopRecording(); } catch { }
        _capture.RecordingStopped -= OnStopped;
        _capture.Dispose();
    }

    /// <summary>Keeps channels 0 and 1 of a multi-channel stream (front left/right).</summary>
    private sealed class FirstTwoChannelsProvider : ISampleProvider
    {
        private readonly ISampleProvider _src;
        private float[] _tmp = Array.Empty<float>();
        public WaveFormat WaveFormat { get; }
        public FirstTwoChannelsProvider(ISampleProvider src)
        {
            _src = src;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(src.WaveFormat.SampleRate, 2);
        }
        public int Read(float[] buffer, int offset, int count)
        {
            int ch = _src.WaveFormat.Channels, frames = count / 2;
            if (_tmp.Length < frames * ch) _tmp = new float[frames * ch];
            int got = _src.Read(_tmp, 0, frames * ch) / ch;
            for (int i = 0; i < got; i++)
            {
                buffer[offset + i * 2] = _tmp[i * ch];
                buffer[offset + i * 2 + 1] = _tmp[i * ch + 1];
            }
            return got * 2;
        }
    }
}
