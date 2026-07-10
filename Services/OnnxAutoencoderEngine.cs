using System.IO;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace SomniumWeaver.Services;

/// <summary>
/// anomaly engine backed by a small autoencoder exported to ONNX.
///
/// the trick that makes one model work on any machine: each signal is standardized online
/// (per-feature EWMA mean/std -> ~N(0,1)) before it hits the model, exactly like the z-score
/// engine does. the autoencoder is trained on standardized "normal" data, so it learns the
/// SHAPE of normal cross-signal correlations (e.g. high load usually comes with high temp).
/// reconstruction error spikes when that joint pattern breaks -- e.g. temp high while load is
/// normal -- which a per-signal z-score would miss. missing signals (no admin -> no cpu temp)
/// are imputed as 0 (the standardized mean), so it degrades gracefully.
///
/// if the model or metadata can't be loaded, <see cref="Available"/> is false and the
/// collector falls back to the z-score engine.
/// </summary>
public sealed class OnnxAutoencoderEngine : IAnomalyEngine, IDisposable
{
    public string Name => "autoencoder";
    public bool Available { get; }
    public string? LastError { get; }

    private readonly InferenceSession? _session;
    private readonly string _inputName = "input";
    private readonly string[] _features = Array.Empty<string>();
    private readonly double _alpha = 0.05;
    private readonly double _baseThreshold = 0.1;

    // per-feature online standardization (shared with the z-score engine)
    private readonly OnlineStandardizer[] _std;

    private readonly int _warmup;
    private readonly int _cooldownTicks;
    private long _samples;
    private int _cooldown;

    public OnnxAutoencoderEngine(string modelPath, string metaPath,
                                 int warmup = 30, int cooldownTicks = 6)
    {
        _warmup = warmup;
        _cooldownTicks = cooldownTicks;

        try
        {
            // ---- metadata ----
            using (var doc = JsonDocument.Parse(File.ReadAllText(metaPath)))
            {
                var root = doc.RootElement;
                _features = root.GetProperty("features").EnumerateArray()
                                .Select(e => e.GetString() ?? "").ToArray();
                if (root.TryGetProperty("alpha", out var a)) _alpha = a.GetDouble();
                if (root.TryGetProperty("threshold", out var t)) _baseThreshold = t.GetDouble();
                if (root.TryGetProperty("input_name", out var n)) _inputName = n.GetString() ?? "input";
            }

            // ---- model ----
            _session = new InferenceSession(modelPath);
            // trust the actual graph's input name over the metadata if they differ
            _inputName = _session.InputMetadata.Keys.FirstOrDefault() ?? _inputName;

            int n = _features.Length;
            _std = new OnlineStandardizer[n];
            for (int i = 0; i < n; i++) _std[i] = new OnlineStandardizer(_alpha);

            Available = n > 0;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Available = false;
            _std = Array.Empty<OnlineStandardizer>();
        }
    }

    public AnomalyResult Observe(IReadOnlyList<(string name, double value)> signals, double sensitivity)
    {
        if (!Available || _session == null) return AnomalyResult.None;

        _samples++;
        if (_cooldown > 0) _cooldown--;

        int n = _features.Length;
        var input = new DenseTensor<float>(new[] { 1, n });

        float worstDev = 0f;
        string worst = "";

        for (int i = 0; i < n; i++)
        {
            double std0 = 0.0; // standardized value for feature i (0 = missing / mean)
            if (TryGet(signals, _features[i], out double raw))
                std0 = Math.Clamp(_std[i].Standardize(raw), -6.0, 6.0);

            input[0, i] = (float)std0;
            if (Math.Abs(std0) > worstDev) { worstDev = (float)Math.Abs(std0); worst = _features[i]; }
        }

        // ---- inference: reconstruction error ----
        double err;
        try
        {
            using var results = _session.Run(new[] { NamedOnnxValue.CreateFromTensor(_inputName, input) });
            var outArr = results.First().AsEnumerable<float>().ToArray();
            double sum = 0.0;
            int m = Math.Min(n, outArr.Length);
            for (int i = 0; i < m; i++)
            {
                double d = input[0, i] - outArr[i];
                sum += d * d;
            }
            err = sum / Math.Max(1, m);
        }
        catch
        {
            return AnomalyResult.None;
        }

        // sensitivity 2..5 scales the trained threshold (3 = as trained; higher = stricter).
        double threshold = _baseThreshold * (sensitivity / 3.0);
        bool warmedUp = _samples >= _warmup;
        bool fire = warmedUp && _cooldown == 0 && err > threshold;
        if (fire) _cooldown = _cooldownTicks;

        // report the error on a sigma-ish scale so the HUD reads comparably to z-score mode.
        float score = threshold > 0 ? (float)(err / threshold * 3.0) : 0f;
        return new AnomalyResult(fire, score, worst);
    }

    private static bool TryGet(IReadOnlyList<(string name, double value)> signals, string name, out double value)
    {
        for (int i = 0; i < signals.Count; i++)
        {
            if (signals[i].name == name && !double.IsNaN(signals[i].value))
            {
                value = signals[i].value;
                return true;
            }
        }
        value = 0.0;
        return false;
    }

    public void Reset()
    {
        foreach (var s in _std) s.Reset();
        _samples = 0;
        _cooldown = 0;
    }

    public void Dispose() => _session?.Dispose();
}
