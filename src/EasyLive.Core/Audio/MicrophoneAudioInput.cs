using NAudio.CoreAudioApi;

namespace EasyLive.Core.Audio;

/// <summary>Default capture device. Mono microphones are up-mixed to stereo.</summary>
public sealed class MicrophoneAudioInput : CaptureAudioInput
{
    private MicrophoneAudioInput(WasapiCapture capture) : base(capture) { }

    /// <summary>Returns null when this PC has no usable microphone.</summary>
    public static MicrophoneAudioInput? TryCreate()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count == 0) return null;
            return new MicrophoneAudioInput(new WasapiCapture());
        }
        catch { return null; }
    }
}
