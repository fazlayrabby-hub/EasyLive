using NAudio.Wave;

namespace EasyLive.Core.Audio;

/// <summary>Game/system sound: WASAPI loopback of the default render device. Delivers nothing while silent.</summary>
public sealed class LoopbackAudioInput : CaptureAudioInput
{
    public LoopbackAudioInput() : base(new WasapiLoopbackCapture()) { }
}
