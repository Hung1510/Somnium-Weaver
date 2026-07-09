namespace SomniumWeaver.Services;

/// <summary>
/// An immutable snapshot of the machine's vitals at one moment. cheap to copy,
/// safe to hand between the collector thread and the render thread.
///
/// the five positional fields always come from perf counters (they work without admin).
/// the hardware fields come from LibreHardwareMonitor and are <c>NaN</c> when the sensor
/// isn't present or readable (e.g. cpu package temp without admin). the anomaly fields
/// are stamped by the online detector.
/// </summary>
public readonly record struct Metrics(
    float CpuPercent,       // 0..100
    float AvailableRamMb,   // MB free
    float TotalRamMb,       // MB installed
    float NetworkKBps,      // total nic throughput, KB/sec
    DateTime Timestamp)
{
    // ---- hardware sensors (NaN => unavailable) ----
    public float CpuTempC { get; init; } = float.NaN;
    public float CpuMaxCorePercent { get; init; } = float.NaN;
    public float GpuLoadPercent { get; init; } = float.NaN;
    public float GpuTempC { get; init; } = float.NaN;
    public float VramUsedMb { get; init; } = float.NaN;
    public float VramTotalMb { get; init; } = float.NaN;
    public float FanRpm { get; init; } = float.NaN;

    // ---- anomaly detector output ----
    public bool IsAnomaly { get; init; } = false;   // true only on the tick an event fires
    public float AnomalyScore { get; init; } = 0f;   // max z-score across signals (sigma)
    public string AnomalySignal { get; init; } = ""; // which signal drove it

    public static readonly Metrics Empty = new(0f, 0f, 0f, 0f, DateTime.MinValue);

    /// <summary>memory pressure, 0 (all free) .. 1 (full).</summary>
    public float MemoryLoad =>
        TotalRamMb <= 0f ? 0f : Math.Clamp(1f - AvailableRamMb / TotalRamMb, 0f, 1f);

    public bool HasGpu => !float.IsNaN(GpuLoadPercent);
    public bool HasCpuTemp => !float.IsNaN(CpuTempC);
    public bool HasGpuTemp => !float.IsNaN(GpuTempC);
    public bool HasFan => !float.IsNaN(FanRpm);
    public bool HasVram => !float.IsNaN(VramUsedMb) && !float.IsNaN(VramTotalMb) && VramTotalMb > 0f;

    public float GpuLoad => HasGpu ? Math.Clamp(GpuLoadPercent / 100f, 0f, 1f) : 0f;

    /// <summary>hottest available temperature, or NaN if none.</summary>
    public float MaxTempC
    {
        get
        {
            float t = float.NaN;
            if (HasCpuTemp) t = float.IsNaN(t) ? CpuTempC : MathF.Max(t, CpuTempC);
            if (HasGpuTemp) t = float.IsNaN(t) ? GpuTempC : MathF.Max(t, GpuTempC);
            return t;
        }
    }

    /// <summary>thermal load 0..1, mapping ~40C (cool) .. 85C (hot). 0 when no temp sensor.</summary>
    public float ThermalLoad
    {
        get
        {
            float t = MaxTempC;
            if (float.IsNaN(t)) return 0f;
            return Math.Clamp((t - 40f) / 45f, 0f, 1f);
        }
    }
}
