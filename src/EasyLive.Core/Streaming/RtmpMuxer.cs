using FFmpeg.AutoGen;

namespace EasyLive.Core.Streaming;

/// <summary>
/// FLV over RTMP(S) output with one H.264 and one AAC stream. Every blocking FFmpeg call runs under a deadline
/// enforced by the AVIO interrupt callback, and <see cref="Abort"/> unblocks any call in progress.
/// Writes happen on the send thread only.
/// </summary>
internal sealed unsafe class RtmpMuxer : IDisposable
{
    private readonly AVIOInterruptCB_callback _interrupt;   // must outlive the context
    private AVFormatContext* _fmt;
    private long _deadline = long.MaxValue;                 // Environment.TickCount64
    private volatile bool _abort;

    private RtmpMuxer()
    {
        _interrupt = _ => _abort || Environment.TickCount64 > Interlocked.Read(ref _deadline) ? 1 : 0;
    }

    public int VideoIndex { get; private set; }
    public int AudioIndex { get; private set; }
    public AVRational VideoTimeBase => _fmt->streams[VideoIndex]->time_base;
    public AVRational AudioTimeBase => _fmt->streams[AudioIndex]->time_base;

    /// <summary>Connects, publishes and writes the FLV header. On failure returns null with the FFmpeg error and
    /// the error lines FFmpeg logged meanwhile (for <see cref="ErrorTexts.ForConnectFailure"/>).</summary>
    public static RtmpMuxer? Open(string url, AVCodecContext* video, AVCodecContext* audio, int timeoutMs,
        CancellationToken ct, out int error, out IReadOnlyList<string> errorLines)
    {
        var m = new RtmpMuxer();
        using var reg = ct.Register(() => m._abort = true);
        FfmpegRuntime.BeginCapture();
        try
        {
            error = m.OpenCore(url, video, audio, timeoutMs);
        }
        finally
        {
            errorLines = FfmpegRuntime.EndCapture();
        }
        if (error < 0 || ct.IsCancellationRequested)
        {
            m.Dispose();
            return null;
        }
        return m;
    }

    private int OpenCore(string url, AVCodecContext* video, AVCodecContext* audio, int timeoutMs)
    {
        AVFormatContext* fmt = null;
        int r = ffmpeg.avformat_alloc_output_context2(&fmt, null, "flv", url);
        if (r < 0) return r;
        _fmt = fmt;
        fmt->interrupt_callback.callback = _interrupt;
        fmt->interrupt_callback.opaque = null;

        r = AddStream(video, out int vi); if (r < 0) return r;
        r = AddStream(audio, out int ai); if (r < 0) return r;
        VideoIndex = vi;
        AudioIndex = ai;

        SetDeadline(timeoutMs);
        AVDictionary* ioOpts = null;
        ffmpeg.av_dict_set(&ioOpts, "tcp_nodelay", "1", 0);
        r = ffmpeg.avio_open2(&fmt->pb, url, ffmpeg.AVIO_FLAG_WRITE, &fmt->interrupt_callback, &ioOpts);
        ffmpeg.av_dict_free(&ioOpts);
        if (r < 0) return r;

        AVDictionary* muxOpts = null;
        ffmpeg.av_dict_set(&muxOpts, "flvflags", "no_duration_filesize", 0);   // live: no seeking back at the end
        r = ffmpeg.avformat_write_header(fmt, &muxOpts);
        ffmpeg.av_dict_free(&muxOpts);
        ClearDeadline();
        return r < 0 ? r : 0;
    }

    private int AddStream(AVCodecContext* enc, out int index)
    {
        index = -1;
        AVStream* st = ffmpeg.avformat_new_stream(_fmt, null);
        if (st == null) return ffmpeg.AVERROR_UNKNOWN;
        int r = ffmpeg.avcodec_parameters_from_context(st->codecpar, enc);
        if (r < 0) return r;
        st->time_base = enc->time_base;
        index = st->index;
        return 0;
    }

    /// <summary>Writes one packet (takes ownership of its data). Fails if it blocks longer than <paramref name="timeoutMs"/>.</summary>
    public int Write(AVPacket* pkt, int timeoutMs)
    {
        SetDeadline(timeoutMs);
        try { return ffmpeg.av_interleaved_write_frame(_fmt, pkt); }
        finally { ClearDeadline(); }
    }

    /// <summary>Makes any blocking call return promptly (and all further ones fail).</summary>
    public void Abort() => _abort = true;

    /// <summary>Flushes the interleaving buffer and writes the FLV trailer (best effort, bounded).</summary>
    public int WriteTrailer(int timeoutMs)
    {
        if (_fmt == null || _fmt->pb == null) return 0;
        SetDeadline(timeoutMs);
        try { return ffmpeg.av_write_trailer(_fmt); }
        finally { ClearDeadline(); }
    }

    private void SetDeadline(int ms) => Interlocked.Exchange(ref _deadline, Environment.TickCount64 + ms);
    private void ClearDeadline() => Interlocked.Exchange(ref _deadline, long.MaxValue);

    /// <summary>Closes the connection and frees the context. Call only when no other thread uses the muxer.</summary>
    public void Dispose()
    {
        var fmt = _fmt;
        _fmt = null;
        if (fmt == null) return;
        SetDeadline(1000);   // RTMP close sends FCUnpublish/deleteStream; never wait long
        if (fmt->pb != null) ffmpeg.avio_closep(&fmt->pb);
        ffmpeg.avformat_free_context(fmt);
        ClearDeadline();
    }
}
