using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace SomniumWeaver.Services;

/// <summary>
/// polls windows performance counters on a background timer and exposes the latest reading
/// as an immutable <see cref="Metrics"/> snapshot. it also owns the optional hardware
/// sensor source (LibreHardwareMonitor) and the online anomaly detector, so consumers get
/// one enriched snapshot from one place.
///
/// design notes:
///  - the very first read of "% Processor Time" always returns 0, so we prime it.
///  - perf counters (cpu/ram/net) are the always-works baseline (no admin needed).
///  - hardware sensors (gpu/temps/fans) are layered on top and degrade to NaN if missing.
///  - the anomaly detector runs here, at the 2Hz sample cadence -- not per render frame.
/// </summary>
public sealed class DataCollector : IDisposable
{
    private const int IntervalMs = 500;

    private readonly Settings _settings;
    private readonly HardwareMonitor _hardware = new();
    private IAnomalyEngine _anomaly;
    private AnomalyEngineKind _engineKind;

    private PerformanceCounter? _cpu;
    private PerformanceCounter? _ram;
    private readonly List<PerformanceCounter> _net = new();

    private System.Threading.Timer? _timer;
    private readonly object _gate = new();
    private Metrics _latest = Metrics.Empty;
    private int _sampling; // re-entrancy guard: LHM polls can exceed the 500ms interval

    private readonly float _totalRamMb;

    public bool IsRunning { get; private set; }
    public string? HardwareError => _hardware.LastError;
    public string AnomalyEngineName => _anomaly.Name;

    /// <summary>raised on the timer thread whenever a fresh sample lands.</summary>
    public event Action<Metrics>? Updated;

    public DataCollector(Settings settings)
    {
        _settings = settings;
        _engineKind = settings.AnomalyEngine;
        _anomaly = CreateEngine(_engineKind);
        _totalRamMb = QueryTotalRamMb();
        TryInitCounters();
    }

    /// <summary>build the requested engine; fall back to z-score if the ONNX model is absent.</summary>
    private static IAnomalyEngine CreateEngine(AnomalyEngineKind kind)
    {
        if (kind == AnomalyEngineKind.Autoencoder)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "model");
            var onnx = new OnnxAutoencoderEngine(
                Path.Combine(dir, "anomaly_autoencoder.onnx"),
                Path.Combine(dir, "anomaly_meta.json"));
            if (onnx.Available) return onnx;
            onnx.Dispose(); // model missing/broken -> fall through
        }
        return new ZScoreAnomalyEngine();
    }

    private void TryInitCounters()
    {
        // cpu + ram. english counter names live under the 009 registry key even on
        // localized installs, so this normally works in VN too.
        try
        {
            _cpu = new PerformanceCounter("Processor", "% Processor Time", "_Total", readOnly: true);
            _cpu.NextValue(); // prime -> discard the mandatory first 0
        }
        catch { _cpu = null; }

        try
        {
            _ram = new PerformanceCounter("Memory", "Available MBytes", readOnly: true);
        }
        catch { _ram = null; }

        // network is best-effort. sum "Bytes Total/sec" across every real nic.
        try
        {
            var cat = new PerformanceCounterCategory("Network Interface");
            foreach (var instance in cat.GetInstanceNames())
            {
                if (instance.Contains("Loopback", StringComparison.OrdinalIgnoreCase))
                    continue;
                var c = new PerformanceCounter("Network Interface", "Bytes Total/sec", instance, readOnly: true);
                c.NextValue(); // prime
                _net.Add(c);
            }
        }
        catch { /* no network counters -> NetworkKBps stays 0 */ }
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;
        _timer = new System.Threading.Timer(_ => Sample(), null, 0, IntervalMs);
    }

    public void Stop()
    {
        IsRunning = false;
        _timer?.Dispose();
        _timer = null;
    }

    private void Sample()
    {
        // if a previous (slow) tick is still running, skip this one instead of racing it.
        if (Interlocked.Exchange(ref _sampling, 1) == 1) return;
        try
        {
            float cpu = Math.Clamp(_cpu?.NextValue() ?? 0f, 0f, 100f);
            float availMb = _ram?.NextValue() ?? 0f;

            float bytesPerSec = 0f;
            foreach (var c in _net)
            {
                try { bytesPerSec += c.NextValue(); } catch { /* nic vanished */ }
            }
            float netKBps = bytesPerSec / 1024f;
            float memLoad = _totalRamMb > 0f ? Math.Clamp(1f - availMb / _totalRamMb, 0f, 1f) : 0f;

            // ---- optional hardware sensors (lazy open / close on toggle) ----
            var hw = HardwareSample.None;
            if (_settings.EnableHardwareSensors)
            {
                if (!_hardware.IsOpen) _hardware.Open();
                hw = _hardware.Poll();
            }
            else if (_hardware.IsOpen)
            {
                _hardware.Close();
            }

            // ---- anomaly detection over whatever signals we actually have ----
            var signals = new List<(string, double)>(6)
            {
                ("cpu", cpu),
                ("ram", memLoad * 100f),
                ("net", netKBps),
            };
            if (!float.IsNaN(hw.GpuLoadPercent)) signals.Add(("gpu", hw.GpuLoadPercent));
            if (!float.IsNaN(hw.CpuTempC)) signals.Add(("cpu-temp", hw.CpuTempC));
            if (!float.IsNaN(hw.GpuTempC)) signals.Add(("gpu-temp", hw.GpuTempC));

            // hot-swap the engine if the user changed it in settings
            if (_settings.AnomalyEngine != _engineKind)
            {
                (_anomaly as IDisposable)?.Dispose();
                _engineKind = _settings.AnomalyEngine;
                _anomaly = CreateEngine(_engineKind);
            }

            var anomaly = _anomaly.Observe(signals, _settings.AnomalyThreshold);

            var snapshot = new Metrics(cpu, availMb, _totalRamMb, netKBps, DateTime.UtcNow)
            {
                CpuTempC = hw.CpuTempC,
                CpuMaxCorePercent = hw.CpuMaxCorePercent,
                GpuLoadPercent = hw.GpuLoadPercent,
                GpuTempC = hw.GpuTempC,
                VramUsedMb = hw.VramUsedMb,
                VramTotalMb = hw.VramTotalMb,
                FanRpm = hw.FanRpm,
                IsAnomaly = anomaly.IsAnomaly,
                AnomalyScore = anomaly.Score,
                AnomalySignal = anomaly.Signal,
            };

            lock (_gate) _latest = snapshot;
            if (_settings.LogTelemetryCsv) LogTelemetry(snapshot);
            Updated?.Invoke(snapshot);
        }
        catch
        {
            // never let a bad tick kill the timer.
        }
        finally
        {
            Interlocked.Exchange(ref _sampling, 0);
        }
    }

    /// <summary>thread-safe read of the most recent sample. call this from the render loop.</summary>
    public Metrics GetLatest()
    {
        lock (_gate) return _latest;
    }

    // append the standardized-ish signal vector to a csv so you can capture YOUR machine's
    // normal and retrain the autoencoder on it (see ml/train_autoencoder.py).
    private static string? _telemetryPath;
    private static void LogTelemetry(Metrics m)
    {
        try
        {
            if (_telemetryPath == null)
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SomniumWeaver");
                Directory.CreateDirectory(dir);
                _telemetryPath = Path.Combine(dir, "telemetry.csv");
                if (!File.Exists(_telemetryPath))
                    File.AppendAllText(_telemetryPath, "timestamp,cpu,ram,net,gpu,cpu-temp,gpu-temp\n");
            }

            var ci = CultureInfo.InvariantCulture;
            string line = string.Join(",",
                m.Timestamp.ToString("o", ci),
                m.CpuPercent.ToString(ci),
                (m.MemoryLoad * 100f).ToString(ci),
                m.NetworkKBps.ToString(ci),
                Fmt(m.GpuLoadPercent, ci), Fmt(m.CpuTempC, ci), Fmt(m.GpuTempC, ci)) + "\n";
            File.AppendAllText(_telemetryPath, line);
        }
        catch { /* logging must never break sampling */ }
    }

    private static string Fmt(float v, CultureInfo ci) => float.IsNaN(v) ? "" : v.ToString(ci);

    // ---- total physical memory via GlobalMemoryStatusEx (no extra nuget) ----

    private static float QueryTotalRamMb()
    {
        var status = new MEMORYSTATUSEX();
        if (GlobalMemoryStatusEx(status))
            return status.ullTotalPhys / (1024f * 1024f);
        return 0f;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MEMORYSTATUSEX
    {
        public uint dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

    public void Dispose()
    {
        Stop();
        _cpu?.Dispose();
        _ram?.Dispose();
        foreach (var c in _net) c.Dispose();
        _net.Clear();
        _hardware.Dispose();
        (_anomaly as IDisposable)?.Dispose();
    }
}
