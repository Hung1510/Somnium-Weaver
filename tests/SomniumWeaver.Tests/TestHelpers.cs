namespace SomniumWeaver.Tests;

internal static class TestHelpers
{
    /// <summary>build a signal list from name/value pairs.</summary>
    public static IReadOnlyList<(string name, double value)> Sig(params (string, double)[] s) => s;

    /// <summary>the full six-signal vector the model expects, in any order (matched by name).</summary>
    public static IReadOnlyList<(string name, double value)> Sample(
        double cpu, double ram, double net, double gpu, double cpuTemp, double gpuTemp)
        => new (string, double)[]
        {
            ("cpu", cpu), ("ram", ram), ("net", net),
            ("gpu", gpu), ("cpu-temp", cpuTemp), ("gpu-temp", gpuTemp),
        };

    /// <summary>box-muller gaussian so tests are deterministic under a seeded Random.</summary>
    public static double NextGaussian(this Random r, double mean = 0.0, double std = 1.0)
    {
        double u1 = 1.0 - r.NextDouble();
        double u2 = 1.0 - r.NextDouble();
        double z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + std * z;
    }
}
