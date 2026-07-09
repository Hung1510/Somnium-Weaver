namespace SomniumWeaver.Services;

/// <summary>
/// the default, dependency-free anomaly engine. for each signal it keeps an
/// exponentially-weighted moving average and variance (West's incremental EWMA-variance)
/// and reports how many standard deviations the latest value sits from the running mean.
/// the combined score is the max |z| across signals -> "any vital doing something unusual
/// for THIS machine". it learns continuously, so there are no hardcoded thresholds.
///
/// after a warmup it flags an event when the score crosses the sensitivity (in sigma), then
/// holds off for a cooldown so a sustained spike doesn't machine-gun bursts.
/// </summary>
public sealed class ZScoreAnomalyEngine : IAnomalyEngine
{
    public string Name => "z-score";

    private sealed class Stat
    {
        public double Mean;
        public double Var;
        public bool Init;
    }

    private readonly Dictionary<string, Stat> _stats = new();
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
                s = new Stat();
                _stats[name] = s;
            }

            if (!s.Init)
            {
                s.Mean = value;
                s.Var = 0.0;
                s.Init = true;
                continue; // no z on the first observation of a signal
            }

            // z-score against the CURRENT estimate (predict), then update (learn).
            double std = Math.Sqrt(Math.Max(s.Var, 1e-9));
            double z = Math.Abs(value - s.Mean) / (std + 1e-9);
            if (z > maxZ) { maxZ = (float)z; worst = name; }

            double diff = value - s.Mean;
            double incr = _alpha * diff;
            s.Mean += incr;
            s.Var = (1.0 - _alpha) * (s.Var + diff * incr); // West's incremental EWMA variance
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
