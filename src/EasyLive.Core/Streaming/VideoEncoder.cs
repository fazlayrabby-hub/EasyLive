using FFmpeg.AutoGen;

namespace EasyLive.Core.Streaming;

/// <summary>
/// H.264 encoder session (one resolution/fps). Fed with system-memory NV12 (hardware encoders, h264_mf) or
/// YUV420P (libopenh264) frames. High profile, keyframe every 2 s, no B-frames, CBR with bufsize = 2 × bitrate,
/// BT.709 limited range. Not thread-safe: used only on the encode thread.
/// </summary>
internal sealed unsafe class VideoEncoder : IDisposable
{
    private AVCodecContext* _ctx;

    private VideoEncoder(AVCodecContext* ctx, EncoderKind kind, string codecName, int width, int height, int fps, int kbps)
    {
        _ctx = ctx;
        Kind = kind;
        CodecName = codecName;
        Width = width;
        Height = height;
        Fps = fps;
        Kbps = kbps;
    }

    public EncoderKind Kind { get; }
    public string CodecName { get; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int Kbps { get; private set; }
    public AVCodecContext* Context => _ctx;
    public AVRational TimeBase => _ctx->time_base;
    public AVPixelFormat InputFormat => _ctx->pix_fmt;

    /// <summary>
    /// Whether the FFmpeg wrapper re-reads bit_rate/rc_max_rate/rc_buffer_size on every frame and reconfigures the
    /// running encoder (h264_qsv: MFXVideoENCODE_Reset; h264_nvenc: nvEncReconfigureEncoder). The others are
    /// re-opened at the next frame (keyframe) instead, on the same connection.
    /// </summary>
    public bool SupportsLiveBitrate => Kind is EncoderKind.QuickSync or EncoderKind.Nvenc;

    public static string CodecNameFor(EncoderKind kind) => kind switch
    {
        EncoderKind.QuickSync => "h264_qsv",
        EncoderKind.Amf => "h264_amf",
        EncoderKind.Nvenc => "h264_nvenc",
        // Spec: OpenH264. The FFmpeg build is used if it has libopenh264; otherwise Media Foundation's software encoder.
        _ => ffmpeg.avcodec_find_encoder_by_name("libopenh264") != null ? "libopenh264" : "h264_mf",
    };

    /// <summary>Opens the encoder; returns null (and logs why) if this PC cannot run it.</summary>
    public static VideoEncoder? Open(EncoderKind kind, int width, int height, int fps, int kbps)
    {
        string name = CodecNameFor(kind);
        AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(name);
        if (codec == null)
        {
            FfmpegRuntime.Log("video", $"encoder {name} not in this FFmpeg build");
            return null;
        }
        AVCodecContext* ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (ctx == null) return null;

        ctx->width = width;
        ctx->height = height;
        ctx->time_base = new AVRational { num = 1, den = fps };
        ctx->framerate = new AVRational { num = fps, den = 1 };
        ctx->pix_fmt = name == "libopenh264" ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_NV12;
        ctx->gop_size = fps * 2;
        ctx->keyint_min = fps * 2;
        ctx->max_b_frames = 0;
        ctx->profile = ffmpeg.AV_PROFILE_H264_HIGH;
        ctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;   // FLV needs SPS/PPS as extradata
        ctx->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        ctx->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        ctx->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        ctx->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        SetRate(ctx, kbps);

        AVDictionary* opts = null;
        foreach (var (k, v) in OptionsFor(name)) ffmpeg.av_dict_set(&opts, k, v, 0);
        if (name == "libopenh264")
        {
            ctx->thread_count = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
            ctx->qmax = 51;   // FFmpeg's default qmax (31) would stop rate control from reaching the target
        }
        if (name == "h264_qsv")
            // No NAL HRD signalling: with it, a live bitrate Reset starts a new sequence (new SPS + IDR), which
            // breaks the strict 2 s keyframe cadence. Rate control is still CBR (maxrate == bitrate).
            ctx->strict_std_compliance = ffmpeg.FF_COMPLIANCE_UNOFFICIAL;

        int r = ffmpeg.avcodec_open2(ctx, codec, &opts);
        ffmpeg.av_dict_free(&opts);
        if (r < 0)
        {
            FfmpegRuntime.Log("video", $"{name} {width}x{height}@{fps} {kbps} kbps failed to open: {FfmpegRuntime.Describe(r)}");
            ffmpeg.avcodec_free_context(&ctx);
            return null;
        }
        FfmpegRuntime.Log("video", $"{name} opened {width}x{height}@{fps} {kbps} kbps, extradata {ctx->extradata_size} bytes");
        return new VideoEncoder(ctx, kind, name, width, height, fps, kbps);
    }

    private static IEnumerable<(string, string)> OptionsFor(string name) => name switch
    {
        "h264_qsv" => new[] { ("preset", "veryfast"), ("forced_idr", "1"), ("look_ahead", "0"), ("async_depth", "2") },
        "h264_nvenc" => new[] { ("preset", "p4"), ("tune", "ll"), ("rc", "cbr"), ("forced-idr", "1"), ("no-scenecut", "1") },
        "h264_amf" => new[] { ("usage", "lowlatency"), ("rc", "cbr"), ("quality", "balanced"), ("forced_idr", "1") },
        "libopenh264" => new[] { ("rc_mode", "bitrate"), ("allow_skip_frames", "0") },
        "h264_mf" => new[] { ("rate_control", "cbr"), ("scenario", "live_streaming"), ("hw_encoding", "0") },
        _ => Array.Empty<(string, string)>(),
    };

    private static void SetRate(AVCodecContext* ctx, int kbps)
    {
        long bps = kbps * 1000L;
        ctx->bit_rate = bps;
        ctx->rc_max_rate = bps;          // maxrate == bitrate selects CBR in h264_qsv
        ctx->rc_buffer_size = (int)(bps * 2);
    }

    /// <summary>
    /// Live change (only when <see cref="SupportsLiveBitrate"/>), picked up by the next frame: target and max rate
    /// change, the VBV buffer keeps its size from <see cref="Open"/> (2 × start bitrate) because h264_qsv rejects
    /// a buffer-size change in MFXVideoENCODE_Reset (MFX_ERR_INCOMPATIBLE_VIDEO_PARAM, verified on Iris Xe).
    /// </summary>
    public void SetBitrateLive(int kbps)
    {
        long bps = kbps * 1000L;
        _ctx->bit_rate = bps;
        _ctx->rc_max_rate = bps;
        Kbps = kbps;
    }

    public int Send(AVFrame* frame) => ffmpeg.avcodec_send_frame(_ctx, frame);

    public int Receive(AVPacket* pkt) => ffmpeg.avcodec_receive_packet(_ctx, pkt);

    public ReadOnlySpan<byte> Extradata => _ctx->extradata == null ? default : new ReadOnlySpan<byte>(_ctx->extradata, _ctx->extradata_size);

    public void Dispose()
    {
        var ctx = _ctx;
        _ctx = null;
        if (ctx != null) ffmpeg.avcodec_free_context(&ctx);
    }
}
