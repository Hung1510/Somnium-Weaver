using LibreHardwareMonitor.Hardware;

namespace SomniumWeaver.Services;

/// <summary>extra sensor readings from LibreHardwareMonitor. NaN => not available.</summary>
public readonly record struct HardwareSample(
    float CpuTempC,
    float CpuMaxCorePercent,
    float GpuLoadPercent,
    float GpuTempC,
    float VramUsedMb,
    float VramTotalMb,
    float FanRpm)
{
    public static readonly HardwareSample None =
        new(float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN, float.NaN);
}

/// <summary>
/// thin wrapper over LibreHardwareMonitor. one <see cref="Poll"/> per collector tick
/// updates every sensor and pulls out the handful we visualize.
///
/// heavy caveat: many sensors (esp. cpu package temperature) require the app to run as
/// administrator, because LHM loads a ring0 driver to read them. without admin you'll
/// still usually get gpu load/temp and ram, but cpu temp comes back NaN. we degrade
/// per-sensor rather than failing, and Open() is wrapped so a blocked driver can't crash us.
/// </summary>
public sealed class HardwareMonitor : IDisposable
{
    private Computer? _computer;
    private readonly UpdateVisitor _visitor = new();

    public bool IsOpen { get; private set; }
    public string? LastError { get; private set; }

    public void Open()
    {
        if (IsOpen) return;
        try
        {
            _computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true, // fans live here / on SuperIO subhardware
                IsControllerEnabled = true,
            };
            _computer.Open();
            IsOpen = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsOpen = false;
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }

    public void Close()
    {
        IsOpen = false;
        try { _computer?.Close(); } catch { }
        _computer = null;
    }

    public HardwareSample Poll()
    {
        var c = _computer;
        if (!IsOpen || c == null) return HardwareSample.None;

        try
        {
            c.Accept(_visitor); // updates all hardware + subhardware in one pass

            float cpuTemp = float.NaN, cpuMaxCore = float.NaN;
            float gpuLoad = float.NaN, gpuTemp = float.NaN;
            float vramUsed = float.NaN, vramTotal = float.NaN;
            float fan = float.NaN;

            foreach (var hw in Flatten(c.Hardware))
            {
                switch (hw.HardwareType)
                {
                    case HardwareType.Cpu:
                        ReadCpu(hw, ref cpuTemp, ref cpuMaxCore);
                        break;
                    case HardwareType.GpuNvidia:
                    case HardwareType.GpuAmd:
                    case HardwareType.GpuIntel:
                        ReadGpu(hw, ref gpuLoad, ref gpuTemp, ref vramUsed, ref vramTotal);
                        break;
                }

                // fans can hang off the motherboard, SuperIO, or the gpu -> take the max rpm.
                foreach (var s in hw.Sensors)
                {
                    if (s.SensorType == SensorType.Fan && s.Value is float f && f > 0f)
                        fan = float.IsNaN(fan) ? f : MathF.Max(fan, f);
                }
            }

            return new HardwareSample(cpuTemp, cpuMaxCore, gpuLoad, gpuTemp, vramUsed, vramTotal, fan);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return HardwareSample.None;
        }
    }

    private static void ReadCpu(IHardware hw, ref float temp, ref float maxCore)
    {
        // temperature: prefer a package/tdie sensor, else the hottest core.
        float pkg = float.NaN, coreMax = float.NaN;
        float loadMax = float.NaN;

        foreach (var s in hw.Sensors)
        {
            if (s.Value is not float v) continue;

            if (s.SensorType == SensorType.Temperature)
            {
                if (s.Name.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Tdie", StringComparison.OrdinalIgnoreCase) ||
                    s.Name.Contains("Tctl", StringComparison.OrdinalIgnoreCase))
                    pkg = v;
                else if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    coreMax = float.IsNaN(coreMax) ? v : MathF.Max(coreMax, v);
            }
            else if (s.SensorType == SensorType.Load &&
                     s.Name.Contains("CPU Core", StringComparison.OrdinalIgnoreCase))
            {
                loadMax = float.IsNaN(loadMax) ? v : MathF.Max(loadMax, v);
            }
        }

        temp = !float.IsNaN(pkg) ? pkg : coreMax;
        maxCore = loadMax;
    }

    private static void ReadGpu(IHardware hw, ref float load, ref float temp,
                                ref float vramUsed, ref float vramTotal)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.Value is not float v) continue;

            switch (s.SensorType)
            {
                case SensorType.Load when s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase):
                    load = v;
                    break;
                case SensorType.Temperature when s.Name.Contains("GPU Core", StringComparison.OrdinalIgnoreCase):
                    temp = v;
                    break;
                case SensorType.SmallData when s.Name.Contains("Memory Used", StringComparison.OrdinalIgnoreCase):
                    // prefer dedicated/"GPU Memory Used" over shared; first match wins is fine.
                    if (float.IsNaN(vramUsed)) vramUsed = v;
                    break;
                case SensorType.SmallData when s.Name.Contains("Memory Total", StringComparison.OrdinalIgnoreCase):
                    if (float.IsNaN(vramTotal)) vramTotal = v;
                    break;
            }
        }
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> hardware)
    {
        foreach (var hw in hardware)
        {
            yield return hw;
            foreach (var sub in Flatten(hw.SubHardware))
                yield return sub;
        }
    }

    public void Dispose() => Close();

    /// <summary>walks the tree and calls Update() on every node (LHM's required refresh step).</summary>
    private sealed class UpdateVisitor : IVisitor
    {
        public void VisitComputer(IComputer computer) => computer.Traverse(this);

        public void VisitHardware(IHardware hardware)
        {
            hardware.Update();
            foreach (var sub in hardware.SubHardware) sub.Accept(this);
        }

        public void VisitSensor(ISensor sensor) { }
        public void VisitParameter(IParameter parameter) { }
    }
}
