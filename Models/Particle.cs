namespace SomniumWeaver.Models;

/// <summary>
/// one mote of light in the tapestry. a plain class (no skiasharp dependency) so it
/// stays testable and poolable. color is packed as 0xAARRGGBB and unpacked at draw time.
/// </summary>
public sealed class Particle
{
    public float X, Y;        // position, device pixels
    public float VX, VY;      // velocity, px/sec
    public float Radius;
    public float Life;        // seconds remaining
    public float MaxLife;     // seconds at birth (drives the fade)
    public uint Color;        // 0xAARRGGBB (alpha ignored; opacity is computed)
    public float Phase;       // per-particle turbulence offset
    public bool IsButterfly;
    public float Spin;        // radians/sec, only butterflies swirl

    /// <summary>0 (dead) .. 1 (fresh). eased so motes fade in and out gently.</summary>
    public float Opacity
    {
        get
        {
            if (MaxLife <= 0f) return 0f;
            float t = Math.Clamp(Life / MaxLife, 0f, 1f);
            // fade in over the first 15% of life, hold, fade out over the last 60%.
            float fadeIn = Math.Clamp((1f - t) / 0.15f, 0f, 1f);   // 0->1 shortly after birth
            float fadeOut = Math.Clamp(t / 0.6f, 0f, 1f);          // 1->0 near death
            return Math.Min(fadeIn, fadeOut);
        }
    }

    public bool IsDead => Life <= 0f;

    /// <summary>re-initialize an existing instance (used by the pool to avoid GC).</summary>
    public void Reset()
    {
        X = Y = VX = VY = Radius = Life = MaxLife = Phase = Spin = 0f;
        Color = 0u;
        IsButterfly = false;
    }
}
