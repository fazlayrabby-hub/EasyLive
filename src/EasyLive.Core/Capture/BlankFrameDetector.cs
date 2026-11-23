namespace EasyLive.Core.Capture;

/// <summary>
/// Detects a capture that has gone black. Feed it a sparse BGRA sample of each frame; returns true once every
/// sampled pixel has been black (luma &lt; 8) continuously for at least the threshold.
/// Frozen non-black frames are deliberately not blank (stall detection is a separate concern).
/// </summary>
public sealed class BlankFrameDetector
{
    private const int BlackLumaLimit = 8;

    private readonly TimeSpan _threshold;
    private TimeSpan? _blankStart;

    public BlankFrameDetector(TimeSpan threshold) => _threshold = threshold;

    public bool Observe(ReadOnlySpan<byte> bgraSample, TimeSpan timestamp)
    {
        if (!IsBlank(bgraSample))
        {
            _blankStart = null;
            return false;
        }

        if (_blankStart is not { } start || timestamp < start)
        {
            _blankStart = timestamp;
            start = timestamp;
        }

        return timestamp - start >= _threshold;
    }

    private static bool IsBlank(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length < 4) return false; // no data => not blank

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var luma = (77 * bgra[i + 2] + 150 * bgra[i + 1] + 29 * bgra[i]) >> 8;
            if (luma >= BlackLumaLimit) return false;
        }
        return true;
    }
}
