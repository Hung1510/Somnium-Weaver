namespace SomniumWeaver.Services;

/// <summary>
/// An immutable snapshot of the machine's vitals at one moment. cheap to copy,
/// safe to hand between the collector thread and the render thread.
/// </summary>
public readonly record struct Metrics(
    float CpuPercent,       // 0..100
    float AvailableRamMb,   // MB free
    float TotalRamMb,       // MB installed
    float NetworkKBps,      // total nic throughput, KB/sec
    DateTime Timestamp)
{
    public static readonly Metrics Empty =
        new(0f, 0f, 0f, 0f, DateTime.MinValue);

    /// <summary>memory pressure, 0 (all free) .. 1 (full).</summary>
    public float MemoryLoad =>
        TotalRamMb <= 0f ? 0f : Math.Clamp(1f - AvailableRamMb / TotalRamMb, 0f, 1f);
}
