using EasyLive.Core.Audio;
using EasyLive.Core.Capture;

namespace EasyLive.Core.Streaming;

/// <param name="SendQueueMs">Media waiting to be sent (newest queued DTS minus oldest unsent DTS).</param>
/// <param name="DroppedFramesTotal">Video frames dropped so far (send-queue drops plus ticks skipped because encoding fell behind).</param>
/// <param name="EncodeLagMs">How far the encode thread's current tick is behind real time.</param>
/// <param name="CurrentKbps">The video bitrate last applied to the encoder.</param>
public sealed record StreamHealth(int SendQueueMs, int DroppedFramesTotal, int EncodeLagMs, int CurrentKbps);

/// <summary>
/// One live stream: encodes pushed frames/audio and sends them as FLV over RTMP(S).
/// After <see cref="Failed"/> the session is dead; dispose it and create a new one.
/// </summary>
public interface IStreamSession : IAsyncDisposable
{
    /// <summary>Connects, publishes and starts the encode/send threads. On failure raises <see cref="Failed"/> and
    /// throws <see cref="StreamSessionException"/> with the same plain message.</summary>
    Task StartAsync(string rtmpUrlWithKey, StreamPlan plan, CancellationToken ct);

    /// <summary>Non-blocking: copies the texture (valid only during the capture callback) and returns.</summary>
    void PushVideo(CaptureFrame f);

    /// <summary>Non-blocking: queues the chunk for encoding.</summary>
    void PushAudio(AudioChunk c);

    /// <summary>Changes the video bitrate live, without reconnecting.</summary>
    void SetBitrate(int kbps);

    /// <summary>Restarts the video encoder at a lower rung (new keyframe) on the same connection. The task faults with
    /// <see cref="StreamSessionException"/> if the switch failed (<see cref="Failed"/> is raised too) and is cancelled if
    /// the session stopped first.</summary>
    Task ReinitAsync(QualityRung lower);

    StreamHealth Health { get; }

    /// <summary>Plain-words reason the stream stopped (raised once, on a thread-pool thread).</summary>
    event Action<string>? Failed;
}

/// <summary>Thrown by <see cref="IStreamSession.StartAsync"/>; <see cref="Exception.Message"/> is safe to show.</summary>
public sealed class StreamSessionException : Exception
{
    public StreamSessionException(string plainMessage) : base(plainMessage) { }
}
