using System.Runtime.InteropServices;
using EasyLive.Core.Capture;
using FFmpeg.AutoGen;
using Vortice.Direct3D11;
using Vortice.DXGI;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace EasyLive.Core.Streaming;

/// <summary>
/// Moves captured GPU frames to the encoder. <see cref="Ingest"/> (capture thread, non-blocking apart from queuing
/// a GPU copy) copies the texture into one of three own CPU-readable staging textures and marks it the latest
/// frame (older unread frames are simply overwritten: drop-oldest). <see cref="TryConvertLatest"/> (encode thread)
/// maps the latest copy and converts BGRA → NV12/YUV420P with libswscale, scaling to the rung size.
/// v1 is a CPU conversion path; no D3D11VA interop.
/// </summary>
internal sealed unsafe class FrameConverter : IDisposable
{
    private enum SlotState { Free, Ready, Busy }

    private sealed class Slot
    {
        public required ID3D11Texture2D Texture;
        public int TexWidth, TexHeight;        // full texture size
        public int Width, Height;              // content size of the frame copied in
        public SlotState State;
        public long Seq;
        public bool Stale;                     // belongs to an older device/size; dispose when released
    }

    private readonly object _gate = new();
    private readonly List<Slot> _slots = new();
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private Slot? _latest;
    private long _seq;
    private long _lastConvertedSeq;
    private bool _disposed;
    private bool _warnedFormat;

    // encode-thread only
    private SwsContext* _sws;
    private int _swsSrcW, _swsSrcH, _swsDstW, _swsDstH;
    private AVPixelFormat _swsDstFmt;

    /// <summary>True once at least one frame was copied.</summary>
    public bool HasFrame { get { lock (_gate) return _latest is not null; } }

    /// <summary>Capture thread: copy the (callback-scoped) texture into an own staging texture.</summary>
    public void Ingest(CaptureFrame f)
    {
        if (f.Texture == 0 || f.Width <= 0 || f.Height <= 0) return;
        Marshal.AddRef(f.Texture);
        using var src = new ID3D11Texture2D(f.Texture);   // releases our AddRef
        lock (_gate)
        {
            if (_disposed) return;
            var desc = src.Description;
            if (desc.Format != Format.B8G8R8A8_UNorm)
            {
                if (!_warnedFormat) FfmpegRuntime.Log("video", $"unsupported capture format {desc.Format}");
                _warnedFormat = true;
                return;
            }
            EnsureDevice(src);
            var slot = PickSlot((int)desc.Width, (int)desc.Height);
            _context!.CopyResource(slot.Texture, src);
            slot.Width = Math.Min(f.Width, slot.TexWidth);
            slot.Height = Math.Min(f.Height, slot.TexHeight);
            slot.State = SlotState.Ready;
            slot.Seq = ++_seq;
            _latest = slot;
        }
    }

    private void EnsureDevice(ID3D11Texture2D src)
    {
        using var dev = src.Device;
        if (_device is not null && _device.NativePointer == dev.NativePointer) return;
        foreach (var s in _slots) RetireSlot(s);
        _slots.Clear();
        _latest = null;
        _context?.Dispose();
        _device?.Dispose();
        _device = src.Device;                                   // own reference
        using (var mt = _device.QueryInterfaceOrNull<ID3D11Multithread>())
            mt?.SetMultithreadProtected(true);                  // capture + encode threads share the immediate context
        _context = _device.ImmediateContext;
    }

    private Slot PickSlot(int w, int h)
    {
        foreach (var s in _slots.Where(s => s.TexWidth != w || s.TexHeight != h).ToList())
        {
            _slots.Remove(s);
            if (_latest == s) _latest = null;
            RetireSlot(s);
        }
        var pick = _slots.FirstOrDefault(s => s.State == SlotState.Free)
                   ?? _slots.FirstOrDefault(s => s.State == SlotState.Ready && s != _latest);
        if (pick is not null) return pick;
        if (_slots.Count < 3)
        {
            var tex = _device!.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
            });
            var slot = new Slot { Texture = tex, TexWidth = w, TexHeight = h };
            _slots.Add(slot);
            return slot;
        }
        return _latest!;   // unreachable with 3 slots (at most one Busy + one latest), kept as a safe fallback
    }

    private static void RetireSlot(Slot s)
    {
        if (s.State == SlotState.Busy) s.Stale = true;   // the encode thread disposes it after Unmap
        else s.Texture.Dispose();
    }

    /// <summary>Makes the next <see cref="TryConvertLatest"/> convert the latest frame again (new output size).</summary>
    public void ReconvertLatest()
    {
        lock (_gate) _lastConvertedSeq = 0;
    }

    /// <summary>
    /// Encode thread: if a frame newer than the last converted one exists, converts it into <paramref name="dst"/>
    /// (which must already be allocated with its format/width/height). Returns false if nothing new (keep the old picture).
    /// </summary>
    public bool TryConvertLatest(AVFrame* dst)
    {
        Slot slot;
        ID3D11DeviceContext context;
        lock (_gate)
        {
            if (_disposed || _latest is null || _latest.Seq == _lastConvertedSeq) return false;
            slot = _latest;
            slot.State = SlotState.Busy;
            context = _context!;
            _lastConvertedSeq = slot.Seq;
        }
        bool ok = false;
        try
        {
            var mapped = context.Map(slot.Texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try { ok = Scale((byte*)mapped.DataPointer, (int)mapped.RowPitch, slot.Width, slot.Height, dst); }
            finally { context.Unmap(slot.Texture, 0); }
        }
        finally
        {
            lock (_gate)
            {
                if (slot.Stale) slot.Texture.Dispose();
                else slot.State = slot == _latest ? SlotState.Ready : SlotState.Free;
            }
        }
        return ok;
    }

    private bool Scale(byte* src, int pitch, int w, int h, AVFrame* dst)
    {
        var fmt = (AVPixelFormat)dst->format;
        if (_sws == null || w != _swsSrcW || h != _swsSrcH || dst->width != _swsDstW || dst->height != _swsDstH || fmt != _swsDstFmt)
        {
            if (_sws != null) ffmpeg.sws_freeContext(_sws);
            _sws = ffmpeg.sws_getContext(w, h, AVPixelFormat.AV_PIX_FMT_BGRA, dst->width, dst->height, fmt,
                (int)SwsFlags.SWS_FAST_BILINEAR, null, null, null);
            if (_sws == null) return false;
            (_swsSrcW, _swsSrcH, _swsDstW, _swsDstH, _swsDstFmt) = (w, h, dst->width, dst->height, fmt);
            var coeffs = new int_array4();
            int* bt709 = ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            for (uint i = 0; i < 4; i++) coeffs[i] = bt709[i];
            var dstCoeffs = coeffs;
            // RGB in full range -> BT.709 limited range YUV (matches what the encoder signals).
            ffmpeg.sws_setColorspaceDetails(_sws, ref coeffs, 1, ref dstCoeffs, 0, 0, 1 << 16, 1 << 16);
        }
        if (ffmpeg.av_frame_make_writable(dst) < 0) return false;   // the encoder may still hold the previous picture
        var srcSlice = new[] { src, null, null, null };
        var srcStride = new[] { pitch, 0, 0, 0 };
        return ffmpeg.sws_scale(_sws, srcSlice, srcStride, 0, h, dst->data, dst->linesize) > 0;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var s in _slots) RetireSlot(s);
            _slots.Clear();
            _latest = null;
            _context?.Dispose();
            _context = null;
            _device?.Dispose();
            _device = null;
        }
        if (_sws != null) { ffmpeg.sws_freeContext(_sws); _sws = null; }
    }
}
