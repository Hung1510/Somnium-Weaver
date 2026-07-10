using SomniumWeaver.Services;
using Xunit;
using static SomniumWeaver.Tests.TestHelpers;

namespace SomniumWeaver.Tests;

public class ZScoreAnomalyEngineTests
{
    [Fact]
    public void NoFireDuringWarmup()
    {
        var e = new ZScoreAnomalyEngine(warmup: 30, cooldownTicks: 0);

        for (int i = 0; i < 5; i++)
        {
            // a huge spike on the 5th tick still can't fire -- we're inside the warmup window
            var r = e.Observe(Sig(("cpu", i == 4 ? 500.0 : 50.0)), 3.0);
            Assert.False(r.IsAnomaly);
        }
    }

    [Fact]
    public void FiresOnSpikeAfterWarmup_AndNamesTheSignal()
    {
        var e = new ZScoreAnomalyEngine(warmup: 20, cooldownTicks: 0);
        var rng = new Random(2);

        AnomalyResult steady = default;
        for (int i = 0; i < 40; i++)
            steady = e.Observe(Sig(
                ("cpu", 50.0 + (rng.NextDouble() * 4 - 2)),
                ("ram", 40.0 + (rng.NextDouble() * 4 - 2))), 3.0);

        Assert.False(steady.IsAnomaly); // baseline noise stays under 3 sigma

        var spike = e.Observe(Sig(("cpu", 300.0), ("ram", 40.0)), 3.0);
        Assert.True(spike.IsAnomaly);
        Assert.Equal("cpu", spike.Signal);
    }

    [Fact]
    public void CooldownSuppressesImmediateRefire()
    {
        var e = new ZScoreAnomalyEngine(warmup: 5, cooldownTicks: 5);
        var rng = new Random(3);
        for (int i = 0; i < 15; i++) e.Observe(Sig(("cpu", 50.0 + (rng.NextDouble() * 4 - 2))), 3.0);

        Assert.True(e.Observe(Sig(("cpu", 300.0)), 3.0).IsAnomaly);   // first event fires
        Assert.False(e.Observe(Sig(("cpu", 300.0)), 3.0).IsAnomaly);  // suppressed by cooldown
    }

    [Fact]
    public void NaNSignalsAreIgnored()
    {
        var e = new ZScoreAnomalyEngine(warmup: 5, cooldownTicks: 0);
        for (int i = 0; i < 10; i++) e.Observe(Sig(("cpu", 50.0), ("gpu", double.NaN)), 3.0);

        var r = e.Observe(Sig(("cpu", 50.0), ("gpu", double.NaN)), 3.0);
        Assert.False(r.IsAnomaly); // constant cpu -> z 0; NaN gpu skipped, no crash
    }

    [Fact]
    public void ResetRestoresWarmup()
    {
        var e = new ZScoreAnomalyEngine(warmup: 20, cooldownTicks: 0);
        var rng = new Random(4);
        for (int i = 0; i < 40; i++) e.Observe(Sig(("cpu", 50.0 + (rng.NextDouble() * 2 - 1))), 3.0);
        Assert.True(e.Observe(Sig(("cpu", 300.0)), 3.0).IsAnomaly);

        e.Reset();

        AnomalyResult r = default;
        for (int i = 0; i < 5; i++) r = e.Observe(Sig(("cpu", 300.0 + i)), 3.0);
        Assert.False(r.IsAnomaly); // back inside a fresh warmup window
    }
}
