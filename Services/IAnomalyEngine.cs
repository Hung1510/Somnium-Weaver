namespace SomniumWeaver.Services;

public readonly record struct AnomalyResult(bool IsAnomaly, float Score, string Signal)
{
    public static readonly AnomalyResult None = new(false, 0f, "");
}

/// <summary>
/// a pluggable anomaly engine. both the dependency-free z-score detector and the ONNX
/// autoencoder implement this, so <see cref="DataCollector"/> can swap between them.
/// </summary>
public interface IAnomalyEngine
{
    string Name { get; }

    /// <summary>
    /// feed the current signals (NaN/missing ones are skipped or imputed) and a sensitivity
    /// (2..5, higher = fewer bursts). returns whether THIS tick is an anomaly event.
    /// </summary>
    AnomalyResult Observe(IReadOnlyList<(string name, double value)> signals, double sensitivity);

    void Reset();
}
