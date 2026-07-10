namespace SomniumWeaver.Services;

/// <summary>
/// the default, dependency-free anomaly engine. each signal gets its own
/// <see cref="OnlineStandardizer"/>; the combined score is the max |z| across signals --
/// "any vital doing something unusual for THIS machine". it learns continuously, so there
/// are no hardcoded thresholds.
///
/// after a warmup it flags an event when the score crosses the sensitivity (in sigma), then
/// holds off for a cooldown so a sustained spike doesn't machine-gun bursts.
/// </summary>
public sealed class ZScoreAnomalyEngine : IAnomalyEngine
{
    public string Name => "z-score";

    private readonly Dictionary<string, OnlineStandardizer> _stats = new();
    private readonly double _alpha;      // learning rate (higher = adapts faster)
    private readonly int _warmup;        // samples before flagging anything
    private readonly int _cooldownTicks; // ticks to stay quiet after an event

    private long _samples;
    private int _cooldown;

    public ZScoreAnomalyEngine(double alpha = 0.05, int warmup = 30, int cooldownTicks = 6)
    {
        _alpha = alpha;
        _warmup = warmup;
        _cooldownTicks = cooldownTicks;
    }

    public AnomalyResult Observe(IReadOnlyList<(string name, double value)> signals, double sensitivity)
    {
        _samples++;
        if (_cooldown > 0) _cooldown--;

        float maxZ = 0f;
        string worst = "";

        foreach (var (name, value) in signals)
        {
            if (double.IsNaN(value)) continue;

            if (!_stats.TryGetValue(name, out var s))
            {
                s = new OnlineStandardizer(_alpha);
                _stats[name] = s;
            }

            // first observation of a signal returns 0 -> harmlessly can't raise the max.
            float z = (float)Math.Abs(s.Standardize(value));
            if (z > maxZ) { maxZ = z; worst = name; }
        }

        bool warmedUp = _samples >= _warmup;
        bool fire = warmedUp && _cooldown == 0 && maxZ >= sensitivity;
        if (fire) _cooldown = _cooldownTicks;

        return new AnomalyResult(fire, maxZ, worst);
    }

    public void Reset()
    {
        _stats.Clear();
        _samples = 0;
        _cooldown = 0;
    }
}
