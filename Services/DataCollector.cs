using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SomniumWeaver.Services;

/// <summary>
/// polls windows performance counters on a background timer and exposes the
/// latest reading as an immutable <see cref="Metrics"/> snapshot.
///
/// design notes:
///  - the very first read of "% Processor Time" always returns 0, so we prime it.
///  - counters are read off the ui thread; the render loop just calls GetLatest().
///  - network + counter creation is defensive: if a category is missing (locale,
///    disabled perf counters, etc.) we degrade gracefully instead of crashing.
/// </summary>
public sealed class DataCollector : IDisposable
{
    private const int IntervalMs = 500;

    private PerformanceCounter? _cpu;
    private PerformanceCounter? _ram;
    private readonly List<PerformanceCounter> _net = new();

    private System.Threading.Timer? _timer;
    private readonly object _gate = new();
    private Metrics _latest = Metrics.Empty;

    private readonly float _totalRamMb;

    public bool IsRunning { get; private set; }

    /// <summary>raised on the timer thread whenever a fresh sample lands.</summary>
    public event Action<Metrics>? Updated;

    public DataCollector()
    {
        _totalRamMb = QueryTotalRamMb();
        TryInitCounters();
    }

    private void TryInitCounters()
    {
        // cpu + ram. these use english counter names, which windows keeps under the
        // 009 registry key even on localized installs, so this normally works in VN too.
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
        // fire once immediately, then every IntervalMs.
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
        try
        {
            float cpu = _cpu?.NextValue() ?? 0f;
            cpu = Math.Clamp(cpu, 0f, 100f);

            float availMb = _ram?.NextValue() ?? 0f;

            float bytesPerSec = 0f;
            foreach (var c in _net)
            {
                try { bytesPerSec += c.NextValue(); } catch { /* nic vanished */ }
            }

            var snapshot = new Metrics(
                CpuPercent: cpu,
                AvailableRamMb: availMb,
                TotalRamMb: _totalRamMb,
                NetworkKBps: bytesPerSec / 1024f,
                Timestamp: DateTime.UtcNow);

            lock (_gate) _latest = snapshot;
            Updated?.Invoke(snapshot);
        }
        catch
        {
            // never let a bad tick kill the timer.
        }
    }

    /// <summary>thread-safe read of the most recent sample. call this from the render loop.</summary>
    public Metrics GetLatest()
    {
        lock (_gate) return _latest;
    }

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
    }
}
