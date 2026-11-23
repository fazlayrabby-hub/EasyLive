using System.Diagnostics;
using System.Runtime.InteropServices;
using EasyLive.Core.Audio;
using EasyLive.Core.Capture;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace EasyLive.Core.Tests.Streaming;

/// <summary>
/// A D3D11 BGRA texture delivered as <see cref="CaptureFrame"/>s at ~<c>fps</c> events/s from its own thread, like
/// WGC (the pointer is only valid during the callback). Pattern: a moving gradient, or changing noise (hard to
/// compress, so the encoder actually needs its full bitrate). Optional freeze window with no events at all.
/// </summary>
internal sealed class SyntheticFrameSource : IDisposable
{
    private const int NoiseFrames = 12;
    private const int Travel = 512;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11Texture2D _texture;
    private readonly int _w, _h;
    private readonly bool _noise;
    private readonly byte[][] _buffers;

    public SyntheticFrameSource(int width, int height, bool noise)
    {
        _w = width;
        _h = height;
        _noise = noise;
        _device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        using (var mt = _device.QueryInterface<ID3D11Multithread>()) mt.SetMultithreadProtected(true);
        _context = _device.ImmediateContext;
        _texture = _device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
        });

        if (noise)
        {
            // 8x8 blocks of random colour, new every frame: demanding (no motion to exploit) but compressible, so a
            // bitrate cap visibly changes the output size.
            var rnd = new Random(42);
            _buffers = Enumerable.Range(0, NoiseFrames).Select(_ =>
            {
                var b = new byte[width * height * 4];
                var colour = new byte[4];
                for (int by = 0; by < height; by += 8)
                    for (int bx = 0; bx < width; bx += 8)
                    {
                        rnd.NextBytes(colour);
                        for (int y = by; y < Math.Min(by + 8, height); y++)
                            for (int x = bx; x < Math.Min(bx + 8, width); x++)
                            {
                                int i = (y * width + x) * 4;
                                b[i] = colour[0]; b[i + 1] = colour[1]; b[i + 2] = colour[2]; b[i + 3] = 255;
                            }
                    }
                return b;
            }).ToArray();
        }
        else
        {
            int wide = width + Travel;
            var b = new byte[wide * height * 4];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < wide; x++)
                {
                    int i = (y * wide + x) * 4;
                    b[i] = (byte)(x & 255);
                    b[i + 1] = (byte)(y * 255 / height);
                    b[i + 2] = (byte)((x * 2 + y) & 255);
                    b[i + 3] = 255;
                }
            _buffers = new[] { b };
        }
    }

    public event Action<CaptureFrame>? FrameArrived;
    public int Delivered;

    /// <summary>Delivers frames until <paramref name="ct"/> fires, pausing during the freeze window.</summary>
    public Task RunAsync(int fps, TimeSpan freezeAt, TimeSpan freezeFor, CancellationToken ct)
    {
        var done = new TaskCompletionSource();
        var t = new Thread(() =>
        {
            try { Loop(fps, freezeAt, freezeFor, ct); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true, Name = "synthetic frames" };
        t.Start();
        return done.Task;
    }

    private unsafe void Loop(int fps, TimeSpan freezeAt, TimeSpan freezeFor, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        for (long i = 0; !ct.IsCancellationRequested; i++)
        {
            var due = TimeSpan.FromSeconds(i / (double)fps);
            while (sw.Elapsed < due && !ct.IsCancellationRequested) Thread.Sleep(1);
            if (sw.Elapsed >= freezeAt && sw.Elapsed < freezeAt + freezeFor) continue;

            byte[] buf = _noise ? _buffers[i % NoiseFrames] : _buffers[0];
            int offset = _noise ? 0 : (int)(i * 8 % Travel);
            uint pitch = (uint)((_noise ? _w : _w + Travel) * 4);
            fixed (byte* p = buf)
                _context.UpdateSubresource(_texture, 0, null, (nint)(p + offset * 4), pitch, 0);
            FrameArrived?.Invoke(new CaptureFrame(_texture.NativePointer, _w, _h, Stopwatch.GetTimestamp()));
            Delivered++;
        }
    }

    public void Dispose()
    {
        _texture.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}

/// <summary>440 Hz sine, 48 kHz stereo float, contiguous 10 ms chunks stamped on the QPC timeline, delivered in real time.</summary>
internal static class SyntheticAudio
{
    public static Task RunAsync(Action<AudioChunk> push, CancellationToken ct) => Task.Factory.StartNew(() =>
    {
        long freq = Stopwatch.Frequency;
        long t0 = Stopwatch.GetTimestamp();
        long sample = 0;
        for (long i = 0; !ct.IsCancellationRequested; i++)
        {
            long chunkStart = t0 + i * freq / 100;
            while (Stopwatch.GetTimestamp() < chunkStart + freq / 100 && !ct.IsCancellationRequested) Thread.Sleep(2);
            var s = new float[960];
            for (int k = 0; k < 480; k++, sample++)
                s[2 * k] = s[2 * k + 1] = 0.2f * MathF.Sin(2 * MathF.PI * 440 * sample / 48000f);
            push(new AudioChunk(s, chunkStart));
        }
    }, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
