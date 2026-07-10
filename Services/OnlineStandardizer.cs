namespace SomniumWeaver.Services;

/// <summary>
/// online per-signal standardization: keeps an exponentially-weighted moving average and
/// variance (West's incremental EWMA-variance) and reports how many standard deviations the
/// latest value sits from the running mean. this is the shared core both anomaly engines use
/// -- the z-score engine takes |value|, the autoencoder clamps it into model input space.
///
/// it adapts continuously, so there are no hardcoded baselines: an idle laptop and a mining
/// rig converge to their own "normal" and both get sensible z-scores.
/// </summary>
public sealed class OnlineStandardizer
{
    private readonly double _alpha; // learning rate (higher = adapts faster)
    private double _mean;
    private double _var;

    public bool Initialized { get; private set; }
    public double Mean => _mean;

    public OnlineStandardizer(double alpha = 0.05) => _alpha = alpha;

    /// <summary>
    /// z-score the value against the CURRENT estimate (predict), then fold it in (learn).
    /// returns 0 on the first observation, when there's no estimate to compare against yet.
    /// </summary>
    public double Standardize(double value)
    {
        if (!Initialized)
        {
            _mean = value;
            _var = 0.0;
            Initialized = true;
            return 0.0;
        }

        double std = Math.Sqrt(Math.Max(_var, 1e-9));
        double z = (value - _mean) / (std + 1e-9);

        double diff = value - _mean;
        double incr = _alpha * diff;
        _mean += incr;
        _var = (1.0 - _alpha) * (_var + diff * incr); // West's incremental EWMA variance

        return z;
    }

    public void Reset()
    {
        _mean = 0.0;
        _var = 0.0;
        Initialized = false;
    }
}
