using System.Diagnostics;
using FFmpeg.AutoGen;

namespace EasyLive.Core.Streaming;

public sealed partial class FfmpegStreamSession
{
    private unsafe void SendMain()
    {
        try
        {
            while (true)
            {
                if (!_queue.TryDequeue(out nint p, 200))
                {
                    if (_queue.IsCompleted) return;   // completed and empty
                    continue;
                }
                var pkt = (AVPacket*)p;
                int r = _muxer!.Write(pkt, WriteTimeoutMs);
                ffmpeg.av_packet_free(&pkt);
                if (r < 0)
                {
                    if (_stopping) return;    // our own shutdown/abort, not a network problem
                    var since = TimeSpan.FromSeconds((Stopwatch.GetTimestamp() - _publishedAt) / (double)Freq);
                    FfmpegRuntime.Log("send", $"write failed after {since.TotalSeconds:F1} s: {FfmpegRuntime.Describe(r)}");
                    RaiseFailed(ErrorTexts.ForSendFailure(since));
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            FfmpegRuntime.Log("send", $"crashed: {ex}");
            RaiseFailed(ErrorTexts.Unexpected);
        }
    }

    /// <summary>Encode thread: applies a requested bitrate change or rung change. False if the session failed.</summary>
    private unsafe bool ApplyPending(ref VideoClock clock, ref long n, long now, AVPacket* scratch)
    {
        int kbps = Interlocked.Exchange(ref _pendingKbps, 0);
        (QualityRung Rung, TaskCompletionSource Done)? reinit;
        lock (_gate)
        {
            reinit = _pendingReinit;
            _pendingReinit = null;
        }

        if (reinit is { } re)
        {
            var r = re.Rung;
            int target = Math.Clamp(kbps > 0 ? kbps : Volatile.Read(ref _currentKbps), r.MinKbps, r.MaxKbps);
            bool ok = ReopenVideo(r.Width, r.Height, r.Fps, target, ref clock, ref n, now, scratch);
            re.Done.TrySetResult();
            if (ok) FfmpegRuntime.Log("encode", $"reinit to {r.Name} at {target} kbps");
            return ok;
        }
        if (kbps <= 0 || kbps == _video!.Kbps) return true;
        if (_video.SupportsLiveBitrate && !_liveBitrateRejected)
        {
            _video.SetBitrateLive(kbps);
            _liveChangeUnverified = true;    // if the next send fails, fall back to re-opening
            Volatile.Write(ref _currentKbps, kbps);
            FfmpegRuntime.Log("encode", $"bitrate {kbps} kbps (live)");
            return true;
        }
        bool reopened = ReopenVideo(_video.Width, _video.Height, _video.Fps, kbps, ref clock, ref n, now, scratch);
        if (reopened) FfmpegRuntime.Log("encode", $"bitrate {kbps} kbps (encoder re-opened)");
        return reopened;
    }

    /// <summary>
    /// Replaces the video encoder on the same connection: drains the old one, opens the new one (its first frame is
    /// an IDR), sends the new SPS/PPS in-band as FLV new extradata and continues the timeline without a gap.
    /// </summary>
    private unsafe bool ReopenVideo(int width, int height, int fps, int kbps, ref VideoClock clock, ref long n, long now,
        AVPacket* scratch, bool drainOld = true)
    {
        var old = _video!;
        if (drainOld && old.Send(null) >= 0 && !DrainVideo(scratch)) return false;   // a broken encoder is just dropped

        var next = VideoEncoder.Open(old.Kind, width, height, fps, kbps);
        if (next is null)
        {
            RaiseFailed(ErrorTexts.EncoderFailed);
            return false;
        }
        AVFrame* frame = AllocVideoFrame(next);
        if (frame == null)
        {
            next.Dispose();
            RaiseFailed(ErrorTexts.EncoderFailed);
            return false;
        }
        old.Dispose();
        _video = next;
        var oldFrame = _videoFrame;
        _videoFrame = frame;
        ffmpeg.av_frame_free(&oldFrame);
        _converter.ReconvertLatest();          // the new-size picture must come from the latest capture, not black
        _attachExtradata = true;

        // Next tick strictly after the last one sent so timestamps keep increasing.
        clock = new VideoClock(clock.TickQpc(0), Freq, fps);
        n = clock.TickIndexAt(now) + 1;
        Volatile.Write(ref _currentKbps, kbps);
        VideoReopenCount++;
        return true;
    }
}
