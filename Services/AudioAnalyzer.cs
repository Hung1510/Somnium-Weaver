using NAudio.CoreAudioApi;
using NAudio.Dsp;
using NAudio.Wave;

namespace SomniumWeaver.Services;

/// <summary>
/// captures whatever the speakers are playing (wasapi loopback), runs an fft, and
/// boils it down to a normalized <see cref="AudioFrame"/> the particle system can read.
///
/// notes:
///  - loopback fires DataAvailable ONLY while audio is playing. during silence we get
///    nothing, so GetLatest() returns Silent once the last packet goes stale.
///  - per-band adaptive gain keeps values in 0..1 across loud and quiet material.
///  - everything runs on naudio's capture thread; the latest frame is swapped under a lock.
/// </summary>
public sealed class AudioAnalyzer : IDisposable
{
    private const int FftSize = 1024;   // 2^10
    private const int FftM = 10;
    private const int HopSize = 512;    // analyze roughly every 512 new samples
    private const double StaleMs = 200; // no packet for this long -> report silence

    private WasapiLoopbackCapture? _capture;

    private readonly float[] _ring = new float[FftSize];
    private int _ringPos;
    private int _sinceAnalyze;

    private readonly Complex[] _fft = new Complex[FftSize];
    private int _sampleRate = 48000;

    // adaptive per-band maxima (slow decay) for normalization
    private float _maxLevel = 1e-4f, _maxBass = 1e-4f, _maxMid = 1e-4f, _maxTreble = 1e-4f;

    // smoothed output envelopes
    private float _envLevel, _envBass, _envMid, _envTreble;

    // beat detection (energy vs rolling average of the bass band)
    private readonly float[] _bassHistory = new float[43]; // ~1s at ~93 hops/sec
    private int _bassHistIdx;
    private DateTime _lastBeatUtc = DateTime.MinValue;

    private readonly object _gate = new();
    private AudioFrame _frame = AudioFrame.Silent;
    private DateTime _lastDataUtc = DateTime.MinValue;

    public bool IsRunning { get; private set; }
    public string? LastError { get; private set; }

    public void Start()
    {
        if (IsRunning) return;
        try
        {
            _capture = new WasapiLoopbackCapture();       // default render device
            _sampleRate = _capture.WaveFormat.SampleRate;
            _capture.DataAvailable += OnDataAvailable;
            _capture.RecordingStopped += (_, _) => { };
            _capture.StartRecording();
            IsRunning = true;
            LastError = null;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            IsRunning = false;
            _capture?.Dispose();
            _capture = null;
        }
    }

    public void Stop()
    {
        IsRunning = false;
        try { _capture?.StopRecording(); } catch { }
        _capture?.Dispose();
        _capture = null;

        lock (_gate)
        {
            _frame = AudioFrame.Silent;
            _lastDataUtc = DateTime.MinValue;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        var cap = _capture;
        if (cap == null) return;
        var fmt = cap.WaveFormat;
        int channels = fmt.Channels;
        if (channels <= 0) return;

        if (fmt.Encoding == WaveFormatEncoding.IeeeFloat && fmt.BitsPerSample == 32)
        {
            int frame = 4 * channels;
            for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                    sum += BitConverter.ToSingle(e.Buffer, i + ch * 4);
                Push(sum / channels);
            }
        }
        else if (fmt.Encoding == WaveFormatEncoding.Pcm && fmt.BitsPerSample == 16)
        {
            int frame = 2 * channels;
            for (int i = 0; i + frame <= e.BytesRecorded; i += frame)
            {
                float sum = 0f;
                for (int ch = 0; ch < channels; ch++)
                    sum += BitConverter.ToInt16(e.Buffer, i + ch * 2) / 32768f;
                Push(sum / channels);
            }
        }
        // any other exotic format -> just ignore; we degrade to silence.
    }

    private void Push(float sample)
    {
        _ring[_ringPos] = sample;
        _ringPos = (_ringPos + 1) % FftSize;
        if (++_sinceAnalyze >= HopSize)
        {
            _sinceAnalyze = 0;
            Analyze();
        }
    }

    private void Analyze()
    {
        // copy the ring in chronological order and apply a hann window.
        float rms = 0f;
        for (int i = 0; i < FftSize; i++)
        {
            float s = _ring[(_ringPos + i) % FftSize];
            rms += s * s;
            float w = (float)FastFourierTransform.HannWindow(i, FftSize);
            _fft[i].X = s * w;
            _fft[i].Y = 0f;
        }
        rms = MathF.Sqrt(rms / FftSize);

        FastFourierTransform.FFT(true, FftM, _fft);

        float binHz = _sampleRate / (float)FftSize;
        float bass = BandEnergy(20f, 250f, binHz);
        float mid = BandEnergy(250f, 2000f, binHz);
        float treble = BandEnergy(2000f, 8000f, binHz);

        // adaptive normalize (fast rise to peaks, slow decay so quiet parts re-sensitize).
        float nLevel = Norm(rms, ref _maxLevel);
        float nBass = Norm(bass, ref _maxBass);
        float nMid = Norm(mid, ref _maxMid);
        float nTreble = Norm(treble, ref _maxTreble);

        // envelopes: quick attack, gentle release.
        _envLevel = Env(_envLevel, nLevel);
        _envBass = Env(_envBass, nBass);
        _envMid = Env(_envMid, nMid);
        _envTreble = Env(_envTreble, nTreble);

        bool beat = DetectBeat(bass);

        var frame = new AudioFrame(_envLevel, _envBass, _envMid, _envTreble, beat);
        lock (_gate)
        {
            _frame = frame;
            _lastDataUtc = DateTime.UtcNow;
        }
    }

    private float BandEnergy(float loHz, float hiHz, float binHz)
    {
        int lo = Math.Max(1, (int)(loHz / binHz));
        int hi = Math.Min(FftSize / 2 - 1, (int)(hiHz / binHz));
        if (hi < lo) return 0f;

        float sum = 0f;
        for (int i = lo; i <= hi; i++)
        {
            float re = _fft[i].X, im = _fft[i].Y;
            sum += MathF.Sqrt(re * re + im * im);
        }
        return sum / (hi - lo + 1);
    }

    private static float Norm(float value, ref float max)
    {
        max = MathF.Max(value, max * 0.995f);
        if (max < 1e-5f) max = 1e-5f;
        return Math.Clamp(value / max, 0f, 1f);
    }

    private static float Env(float current, float target)
    {
        float k = target > current ? 0.55f : 0.15f; // attack vs release
        return current + (target - current) * k;
    }

    private bool DetectBeat(float bassEnergy)
    {
        float avg = 0f;
        for (int i = 0; i < _bassHistory.Length; i++) avg += _bassHistory[i];
        avg /= _bassHistory.Length;

        _bassHistory[_bassHistIdx] = bassEnergy;
        _bassHistIdx = (_bassHistIdx + 1) % _bassHistory.Length;

        bool loudEnough = bassEnergy > avg * 1.4f && bassEnergy > 1e-3f;
        var now = DateTime.UtcNow;
        if (loudEnough && (now - _lastBeatUtc).TotalMilliseconds > 220)
        {
            _lastBeatUtc = now;
            return true;
        }
        return false;
    }

    /// <summary>latest analyzed frame, or Silent if audio has stopped flowing.</summary>
    public AudioFrame GetLatest()
    {
        lock (_gate)
        {
            if ((DateTime.UtcNow - _lastDataUtc).TotalMilliseconds > StaleMs)
                return AudioFrame.Silent;
            return _frame;
        }
    }

    public void Dispose() => Stop();
}
