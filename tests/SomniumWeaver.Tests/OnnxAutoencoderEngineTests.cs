using SomniumWeaver.Services;
using Xunit;
using static SomniumWeaver.Tests.TestHelpers;

namespace SomniumWeaver.Tests;

public class OnnxAutoencoderEngineTests
{
    private static string ModelDir => Path.Combine(AppContext.BaseDirectory, "model");
    private static string ModelPath => Path.Combine(ModelDir, "anomaly_autoencoder.onnx");
    private static string MetaPath => Path.Combine(ModelDir, "anomaly_meta.json");

    [Fact]
    public void ModelLoadsWhenPresent()
    {
        using var e = new OnnxAutoencoderEngine(ModelPath, MetaPath);
        Assert.True(e.Available, e.LastError ?? "model should have loaded");
        Assert.Equal("autoencoder", e.Name);
    }

    [Fact]
    public void MissingModelFallsBackGracefully()
    {
        using var e = new OnnxAutoencoderEngine("does-not-exist.onnx", "does-not-exist.json");
        Assert.False(e.Available);
        // unavailable engine reports no anomaly; the collector then uses the z-score engine
        Assert.Equal(AnomalyResult.None, e.Observe(Sig(("cpu", 1.0)), 3.0));
    }

    [Fact]
    public void FlagsBrokenCorrelationButNotNormal()
    {
        using var e = new OnnxAutoencoderEngine(ModelPath, MetaPath, warmup: 20, cooldownTicks: 0);
        Assert.True(e.Available, e.LastError ?? "model should have loaded");

        var rng = new Random(5);

        // warm up on correlated "normal" telemetry: temps track their load, like real data.
        for (int i = 0; i < 80; i++)
        {
            double cpuN = rng.NextGaussian(0, 4);
            double gpuN = rng.NextGaussian(0, 4);
            e.Observe(Sample(
                cpu: 40 + cpuN,
                ram: 50 + rng.NextGaussian(0, 5),
                net: 20 + rng.NextGaussian(0, 6),
                gpu: 35 + gpuN,
                cpuTemp: 60 + 0.85 * cpuN + rng.NextGaussian(0, 2),
                gpuTemp: 65 + 0.85 * gpuN + rng.NextGaussian(0, 2)), 3.0);
        }

        // a sample sitting on the running mean -> standardized ~0 -> low reconstruction error
        var normal = e.Observe(Sample(40, 50, 20, 35, 60, 65), 3.0);

        // temps slammed high while load stays at the mean -> off the learned manifold
        var broken = e.Observe(Sample(40, 50, 20, 35, 95, 100), 3.0);

        Assert.True(broken.IsAnomaly,
            $"broken correlation should fire (broken score={broken.Score}, normal score={normal.Score})");
        Assert.True(broken.Score > normal.Score, "broken input should score higher than normal");
        Assert.False(normal.IsAnomaly, $"on-manifold sample should not fire (score={normal.Score})");
    }
}
