namespace SomniumWeaver.Services;

/// <summary>
/// one analyzed slice of the system audio. all bands are normalized 0..1 via the
/// analyzer's adaptive gain, so they stay useful whether the music is loud or quiet.
/// </summary>
public readonly record struct AudioFrame(
    float Level,   // overall loudness (rms), 0..1
    float Bass,    // ~20-250 Hz
    float Mid,     // ~250-2000 Hz
    float Treble,  // ~2-8 kHz
    bool Beat)     // true on the frame a beat was detected
{
    public static readonly AudioFrame Silent = new(0f, 0f, 0f, 0f, false);
}
