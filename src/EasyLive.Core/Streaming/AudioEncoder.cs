using FFmpeg.AutoGen;

namespace EasyLive.Core.Streaming;

/// <summary>
/// AAC-LC 128 kbps, 48 kHz stereo. Takes interleaved float samples already placed on the session timeline
/// (<see cref="AudioTimeline"/>), buffers them into 1024-sample planar frames and encodes. Encode thread only.
/// </summary>
internal sealed unsafe class AudioEncoder : IDisposable
{
    public const int SampleRate = 48000;
    private const int Bitrate = 128_000;

    private AVCodecContext* _ctx;
    private AVFrame* _frame;
    private float[] _left = new float[8192];
    private float[] _right = new float[8192];
    private int _count;
    private long _fifoPts;

    private AudioEncoder(AVCodecContext* ctx, AVFrame* frame)
    {
        _ctx = ctx;
        _frame = frame;
    }

    public AVCodecContext* Context => _ctx;
    public AVRational TimeBase => _ctx->time_base;
    private int FrameSize => _ctx->frame_size > 0 ? _ctx->frame_size : 1024;

    public static AudioEncoder? Open()
    {
        AVCodec* codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
        if (codec == null) return null;
        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (ctx == null) return null;
        ctx->sample_rate = SampleRate;
        ctx->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        ffmpeg.av_channel_layout_default(&ctx->ch_layout, 2);
        ctx->bit_rate = Bitrate;
        ctx->time_base = new AVRational { num = 1, den = SampleRate };
        ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
        int r = ffmpeg.avcodec_open2(ctx, codec, null);
        if (r < 0)
        {
            FfmpegRuntime.Log("audio", $"aac failed to open: {FfmpegRuntime.Describe(r)}");
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }
        AVFrame* frame = ffmpeg.av_frame_alloc();
        frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        frame->sample_rate = SampleRate;
        frame->nb_samples = ctx->frame_size;
        ffmpeg.av_channel_layout_copy(&frame->ch_layout, &ctx->ch_layout);
        if (ffmpeg.av_frame_get_buffer(frame, 0) < 0)
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }
        return new AudioEncoder(ctx, frame);
    }

    /// <summary>Appends <paramref name="silenceFrames"/> of silence then the chunk (minus its first
    /// <paramref name="skipFrames"/>), starting at <paramref name="pts"/>; encodes every full frame.</summary>
    /// <returns>Negative FFmpeg error, or 0.</returns>
    public int Write(ReadOnlySpan<float> interleaved, long pts, int skipFrames, int silenceFrames, Action<nint> onPacket, AVPacket* scratch)
    {
        if (_count == 0) _fifoPts = pts;
        for (int left = silenceFrames; left > 0;)   // in pieces: a long gap must not allocate a huge buffer
        {
            int k = Math.Min(left, 4096);
            Ensure(_count + k);
            Array.Clear(_left, _count, k);
            Array.Clear(_right, _count, k);
            _count += k;
            left -= k;
            int r = EncodeFullFrames(onPacket, scratch);
            if (r < 0) return r;
        }
        int frames = interleaved.Length / 2;
        Ensure(_count + Math.Max(0, frames - skipFrames));
        for (int i = skipFrames; i < frames; i++)
        {
            _left[_count] = interleaved[2 * i];
            _right[_count] = interleaved[2 * i + 1];
            _count++;
        }
        return EncodeFullFrames(onPacket, scratch);
    }

    private int EncodeFullFrames(Action<nint> onPacket, AVPacket* scratch)
    {
        while (_count >= FrameSize)
        {
            int r = EncodeFrame(FrameSize, onPacket, scratch);
            if (r < 0) return r;
        }
        return 0;
    }

    /// <summary>Encodes what is buffered and drains the encoder (end of stream).</summary>
    public int Flush(Action<nint> onPacket, AVPacket* scratch)
    {
        if (_count > 0)
        {
            int r = EncodeFrame(_count, onPacket, scratch);   // aac accepts a short last frame
            if (r < 0) return r;
        }
        int s = ffmpeg.avcodec_send_frame(_ctx, null);
        return s < 0 ? s : Drain(onPacket, scratch);
    }

    private int EncodeFrame(int n, Action<nint> onPacket, AVPacket* scratch)
    {
        int r = ffmpeg.av_frame_make_writable(_frame);
        if (r < 0) return r;
        _frame->nb_samples = n;
        new ReadOnlySpan<float>(_left, 0, n).CopyTo(new Span<float>(_frame->data[0], n));
        new ReadOnlySpan<float>(_right, 0, n).CopyTo(new Span<float>(_frame->data[1], n));
        _frame->pts = _fifoPts;
        _fifoPts += n;
        _count -= n;
        Array.Copy(_left, n, _left, 0, _count);
        Array.Copy(_right, n, _right, 0, _count);
        r = ffmpeg.avcodec_send_frame(_ctx, _frame);
        return r < 0 ? r : Drain(onPacket, scratch);
    }

    private int Drain(Action<nint> onPacket, AVPacket* scratch)
    {
        while (true)
        {
            int r = ffmpeg.avcodec_receive_packet(_ctx, scratch);
            if (r == FfErrors.EAgain || r == ffmpeg.AVERROR_EOF) return 0;
            if (r < 0) return r;
            AVPacket* p = ffmpeg.av_packet_alloc();
            ffmpeg.av_packet_move_ref(p, scratch);
            onPacket((nint)p);
        }
    }

    private void Ensure(int needed)
    {
        if (needed <= _left.Length) return;
        int size = Math.Max(needed, _left.Length * 2);
        Array.Resize(ref _left, size);
        Array.Resize(ref _right, size);
    }

    public void Dispose()
    {
        var frame = _frame; _frame = null;
        if (frame != null) ffmpeg.av_frame_free(&frame);
        var ctx = _ctx; _ctx = null;
        if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
    }
}

internal static class FfErrors
{
    /// <summary>AVERROR(EAGAIN) as built for Windows (EAGAIN = 11).</summary>
    public const int EAgain = -11;
}
