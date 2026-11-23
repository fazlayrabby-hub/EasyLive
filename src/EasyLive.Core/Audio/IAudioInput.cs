namespace EasyLive.Core.Audio;

/// <summary>Audio at the pipeline format: 48 kHz, stereo, interleaved float32.
/// <paramref name="QpcTicks"/> is <c>Stopwatch.GetTimestamp()</c> taken at the chunk's first sample.</summary>
public sealed record AudioChunk(float[] Samples, long QpcTicks);

public interface IAudioInput
{
    event Action<AudioChunk> Data;
    /// <summary>The device went away or capture stopped with an error.</summary>
    event Action? Lost;
    void Start();
}
