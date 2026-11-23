namespace EasyLive.Core.Audio;

/// <summary>Pure channel-layout conversion to interleaved stereo (WASAPI channel order).</summary>
public static class ChannelDownmix
{
    private const float Half = 0.70710678f;

    public static float[] ToStereo(float[] interleaved, int channels)
    {
        if (channels == 2) return interleaved;
        int frames = interleaved.Length / channels;
        var o = new float[frames * 2];
        if (channels == 1)
        {
            for (int i = 0; i < frames; i++) o[2 * i] = o[2 * i + 1] = interleaved[i];
            return o;
        }
        // Per-channel (left gain, right gain). Order: FL FR FC LFE BL BR SL SR; 4 ch is quad: FL FR BL BR.
        var gl = new float[channels]; var gr = new float[channels];
        gl[0] = 1; gr[1] = 1;
        if (channels == 4) { gl[2] = Half; gr[3] = Half; }
        else
        {
            gl[2] = gr[2] = Half;                            // centre (dialogue)
            // channel 3 is LFE: ignored
            if (channels > 4) gl[4] = Half;                  // BL
            if (channels > 5) gr[5] = Half;                  // BR
            if (channels > 6) gl[6] = Half;                  // SL
            if (channels > 7) gr[7] = Half;                  // SR
        }
        float sumL = 0, sumR = 0;
        for (int c = 0; c < channels; c++) { sumL += gl[c]; sumR += gr[c]; }
        float norm = 1f / Math.Max(sumL, sumR);               // all-ones input cannot clip
        for (int i = 0; i < frames; i++)
        {
            float l = 0, r = 0;
            for (int c = 0; c < channels; c++)
            {
                float v = interleaved[i * channels + c];
                l += v * gl[c]; r += v * gr[c];
            }
            o[2 * i] = l * norm; o[2 * i + 1] = r * norm;
        }
        return o;
    }
}
