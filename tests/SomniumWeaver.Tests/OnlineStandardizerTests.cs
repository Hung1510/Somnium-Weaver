using SomniumWeaver.Services;
using Xunit;

namespace SomniumWeaver.Tests;

public class OnlineStandardizerTests
{
    [Fact]
    public void FirstObservationReturnsZeroAndInitializes()
    {
        var s = new OnlineStandardizer(0.05);
        Assert.False(s.Initialized);

        double z = s.Standardize(42.0);

        Assert.Equal(0.0, z, 10);
        Assert.True(s.Initialized);
    }

    [Fact]
    public void ConstantStreamNeverDeviates()
    {
        var s = new OnlineStandardizer(0.05);
        for (int i = 0; i < 50; i++)
        {
            double z = s.Standardize(7.0);
            Assert.True(Math.Abs(z) < 1e-6, $"constant input should give ~0, got {z}");
        }
    }

    [Fact]
    public void LargeDeviationYieldsLargeZ_ThenAdaptsDown()
    {
        var rng = new Random(1);
        var s = new OnlineStandardizer(0.05);

        // noisy baseline around 100 (+/- ~1) so std is meaningful, not degenerate
        for (int i = 0; i < 200; i++) s.Standardize(100.0 + (rng.NextDouble() * 2.0 - 1.0));

        double zSpike = s.Standardize(110.0);           // +10 from mean, small std -> large z
        Assert.True(zSpike > 3.0, $"expected a large z on the spike, got {zSpike}");

        // keep feeding the elevated value -> the mean chases it -> z shrinks
        double zLater = zSpike;
        for (int i = 0; i < 100; i++) zLater = s.Standardize(110.0);
        Assert.True(zLater < zSpike, $"z should adapt downward ({zLater} !< {zSpike})");
    }

    [Fact]
    public void ResetClearsState()
    {
        var s = new OnlineStandardizer(0.05);
        s.Standardize(5.0);
        s.Standardize(6.0);

        s.Reset();

        Assert.False(s.Initialized);
        Assert.Equal(0.0, s.Standardize(99.0), 10); // treated as a first observation again
    }
}
