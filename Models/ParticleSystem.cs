using SkiaSharp;
using SomniumWeaver.Services;

namespace SomniumWeaver.Models;

/// <summary>
/// owns and animates the tapestry. everything hot-path here avoids per-frame
/// allocations: particles are pooled, SKPaint objects are reused, no LINQ in Update/Draw.
/// </summary>
public sealed class ParticleSystem
{
    // -- shorekeeper palette (0xRRGGBB) --
    private const uint CalmTeal   = 0x2FBFB3;
    private const uint BrightCyan = 0x9BF6FF;
    private const uint Gold       = 0xFFD700;
    private const uint Pink       = 0xFF69B4;

    // -- tuning --
    private const float ConnectDistance = 92f;   // px, constellation link range
    private const int   LineBudget      = 600;   // skip lines above this active count (O(n^2) guard)
    private const float BurstCooldown   = 2.5f;  // seconds between cpu-anomaly bursts
    private const int   BurstCount      = 30;

    // audio-reactive tuning
    private const float AudioEmitScale     = 140f;  // extra particles/sec at full energy
    private const float AudioBurstCooldown = 0.32f; // min seconds between beat bursts

    private readonly Settings _settings;
    private readonly Random _rng = new();

    private readonly List<Particle> _active = new(1024);
    private readonly Stack<Particle> _pool = new(1024);

    // reused paints -> no GC churn per frame
    private readonly SKPaint _dotPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _glowPaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 6f)
    };
    private readonly SKPaint _linePaint = new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 1f
    };

    private float _emitAccum;
    private float _time;
    private float _burstTimer;
    private float _prevCpu;

    // audio-reactive state (fed from the ui thread each frame before Update)
    private Services.AudioFrame _audio = Services.AudioFrame.Silent;
    private bool _audioOn;
    private float _audioBurstTimer;

    public int Count => _active.Count;

    public ParticleSystem(Settings settings) => _settings = settings;

    /// <summary>set the current audio snapshot + whether audio-reactive mode is on.</summary>
    public void SetAudio(bool on, Services.AudioFrame frame)
    {
        _audioOn = on;
        _audio = frame;
    }

    // ---------------------------------------------------------------- update

    public void Update(float dt, Metrics m, float width, float height)
    {
        if (dt <= 0f || width <= 0f || height <= 0f) return;
        _time += dt;
        _burstTimer += dt;
        _audioBurstTimer += dt;

        HandleEmission(dt, m, width, height);
        HandleAnomaly(m, width, height);
        HandleAudioBeat(width, height);
        Advance(dt, height);

        _prevCpu = m.CpuPercent;
    }

    private void HandleAudioBeat(float width, float height)
    {
        if (!_audioOn || !_audio.Beat) return;
        if (_audioBurstTimer < AudioBurstCooldown) return;
        _audioBurstTimer = 0f;
        TriggerButterflyBurst(width * 0.5f, height * 0.5f);
    }

    private void HandleEmission(float dt, Metrics m, float width, float height)
    {
        // emission rate scales with cpu: rate = cpu% * multiplier (particles/sec).
        float rate = m.CpuPercent * _settings.EmissionMultiplier;

        // audio-reactive mode adds a music-driven stream so it comes alive even at idle cpu.
        if (_audioOn)
            rate += (0.5f * _audio.Level + 0.5f * _audio.Bass) * AudioEmitScale;

        _emitAccum += rate * dt;

        int toSpawn = (int)_emitAccum;
        if (toSpawn <= 0) return;
        _emitAccum -= toSpawn;

        float memLoad = m.MemoryLoad; // 0..1 -> faster drift when ram is tight
        for (int i = 0; i < toSpawn; i++)
        {
            if (_active.Count >= _settings.MaxParticles) break;
            SpawnAmbient(width, height, m.CpuPercent, memLoad);
        }
    }

    private void SpawnAmbient(float width, float height, float cpu, float memLoad)
    {
        var p = Rent();

        p.X = (float)_rng.NextDouble() * width;
        p.Y = height + (float)_rng.NextDouble() * 24f;  // enter from just below the bottom edge

        float speed = 40f + (float)_rng.NextDouble() * 50f;
        speed *= 0.6f + memLoad;                          // ram pressure -> quicker rising
        if (_audioOn) speed *= 1f + _audio.Level * 0.8f;  // louder -> livelier rise

        p.VX = ((float)_rng.NextDouble() - 0.5f) * 30f;   // gentle horizontal drift
        p.VY = -speed;                                    // rise

        p.Radius = 1.6f + (float)_rng.NextDouble() * 2.4f;
        if (_audioOn) p.Radius += _audio.Bass * 3f;       // bass swells the motes

        p.MaxLife = 5f + (float)_rng.NextDouble() * 4f;
        p.Life = p.MaxLife;
        p.Phase = (float)_rng.NextDouble() * MathF.Tau;
        p.IsButterfly = false;
        p.Spin = 0f;

        // hotter cpu (or brighter treble) -> shift the mote toward bright cyan.
        float t = Math.Clamp(cpu / 100f, 0f, 1f);
        if (_audioOn) t = MathF.Max(t, _audio.Treble);
        p.Color = LerpRgb(CalmTeal, BrightCyan, t);

        _active.Add(p);
    }

    private void HandleAnomaly(Metrics m, float width, float height)
    {
        // a "spike" = cpu crossing INTO an extreme band (not merely sitting there),
        // rate-limited by a cooldown so we don't machine-gun bursts.
        bool crossedHigh = _prevCpu <= 80f && m.CpuPercent > 80f;
        bool crossedLow  = _prevCpu >= 15f && m.CpuPercent < 15f;

        if ((crossedHigh || crossedLow) && _burstTimer >= BurstCooldown)
        {
            _burstTimer = 0f;
            TriggerButterflyBurst(width * 0.5f, height * 0.5f);
        }
    }

    /// <summary>emit a swirl of golden/pink "butterflies" that spiral outward for ~2s.</summary>
    public void TriggerButterflyBurst(float cx, float cy)
    {
        for (int i = 0; i < BurstCount; i++)
        {
            if (_active.Count >= _settings.MaxParticles) break;
            var p = Rent();

            float angle = (float)_rng.NextDouble() * MathF.Tau;
            float speed = 60f + (float)_rng.NextDouble() * 90f;

            p.X = cx;
            p.Y = cy;
            p.VX = MathF.Cos(angle) * speed;
            p.VY = MathF.Sin(angle) * speed;
            p.Radius = 8f + (float)_rng.NextDouble() * 4f;      // 8..12 px
            p.MaxLife = 2f;
            p.Life = p.MaxLife;
            p.Phase = angle;
            p.IsButterfly = true;
            p.Spin = ((float)_rng.NextDouble() < 0.5f ? -1f : 1f) * (1.5f + (float)_rng.NextDouble());
            p.Color = (_rng.NextDouble() < 0.5) ? Gold : Pink;

            _active.Add(p);
        }
    }

    private void Advance(float dt, float height)
    {
        // iterate backwards so we can swap-remove dead particles cheaply.
        for (int i = _active.Count - 1; i >= 0; i--)
        {
            var p = _active[i];
            p.Life -= dt;

            if (p.IsButterfly)
            {
                // rotate the velocity vector -> outward spiral, with a little drag.
                float cos = MathF.Cos(p.Spin * dt);
                float sin = MathF.Sin(p.Spin * dt);
                float vx = p.VX * cos - p.VY * sin;
                float vy = p.VX * sin + p.VY * cos;
                p.VX = vx * 0.99f;
                p.VY = vy * 0.99f;
            }
            else
            {
                // organic sideways sway via a slow sine on each mote's own phase.
                p.VX += MathF.Sin(_time * 0.8f + p.Phase) * 6f * dt;
            }

            p.X += p.VX * dt;
            p.Y += p.VY * dt;

            bool offTop = p.Y < -20f;
            if (p.IsDead || offTop)
            {
                // swap-remove
                _active[i] = _active[_active.Count - 1];
                _active.RemoveAt(_active.Count - 1);
                Return(p);
            }
        }
    }

    // ---------------------------------------------------------------- draw

    public void Draw(SKCanvas canvas)
    {
        // transparent wipe -> the desktop shows through.
        canvas.Clear(SKColors.Transparent);

        bool glow = _settings.Quality == QualityLevel.High;

        if (_settings.ConstellationLines &&
            _settings.Quality != QualityLevel.Low &&
            _active.Count <= LineBudget)
        {
            DrawConnections(canvas);
        }

        for (int i = 0; i < _active.Count; i++)
        {
            var p = _active[i];
            float a = p.Opacity;
            if (a <= 0f) continue;

            byte alpha = (byte)(a * 255f);
            var color = Unpack(p.Color, alpha);

            if (glow)
            {
                _glowPaint.Color = Unpack(p.Color, (byte)(alpha * 0.5f));
                canvas.DrawCircle(p.X, p.Y, p.Radius * 1.9f, _glowPaint);
            }

            _dotPaint.Color = color;
            canvas.DrawCircle(p.X, p.Y, p.Radius, _dotPaint);
        }
    }

    private void DrawConnections(SKCanvas canvas)
    {
        float maxSq = ConnectDistance * ConnectDistance;
        int n = _active.Count;

        for (int i = 0; i < n; i++)
        {
            var a = _active[i];
            if (a.IsButterfly) continue;

            for (int j = i + 1; j < n; j++)
            {
                var b = _active[j];
                if (b.IsButterfly) continue;

                float dx = a.X - b.X;
                float dy = a.Y - b.Y;
                float d2 = dx * dx + dy * dy;
                if (d2 > maxSq) continue;

                float closeness = 1f - MathF.Sqrt(d2) / ConnectDistance; // 0..1
                float lineA = closeness * Math.Min(a.Opacity, b.Opacity) * 0.35f;
                if (lineA <= 0.01f) continue;

                _linePaint.Color = Unpack(CalmTeal, (byte)(lineA * 255f));
                canvas.DrawLine(a.X, a.Y, b.X, b.Y, _linePaint);
            }
        }
    }

    // ---------------------------------------------------------------- pool

    private Particle Rent() => _pool.Count > 0 ? _pool.Pop() : new Particle();

    private void Return(Particle p)
    {
        p.Reset();
        // keep the pool bounded so a huge burst doesn't pin memory forever.
        if (_pool.Count < _settings.MaxParticles) _pool.Push(p);
    }

    public void Clear()
    {
        foreach (var p in _active) Return(p);
        _active.Clear();
        _emitAccum = 0f;
    }

    // ---------------------------------------------------------------- color helpers

    private static SKColor Unpack(uint rgb, byte alpha) =>
        new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, alpha);

    private static uint LerpRgb(uint a, uint b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        byte ar = (byte)(a >> 16), ag = (byte)(a >> 8), ab = (byte)a;
        byte br = (byte)(b >> 16), bg = (byte)(b >> 8), bb = (byte)b;
        byte r = (byte)(ar + (br - ar) * t);
        byte g = (byte)(ag + (bg - ag) * t);
        byte bl = (byte)(ab + (bb - ab) * t);
        return (uint)((r << 16) | (g << 8) | bl);
    }

    public void Dispose()
    {
        _dotPaint.Dispose();
        _glowPaint.Dispose();
        _linePaint.Dispose();
    }
}
