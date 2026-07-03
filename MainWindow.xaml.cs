using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SomniumWeaver.Models;
using SomniumWeaver.Services;

namespace SomniumWeaver;

public partial class MainWindow : Window
{
    private readonly Settings _settings;
    private readonly DataCollector _collector;
    private readonly ParticleSystem _system;
    private readonly AudioAnalyzer _audio;

    // render-loop timing (driven by the compositor clock)
    private TimeSpan _lastRenderTime;
    private double _accum;

    // fps meter
    private int _frameCount;
    private double _fpsAccum;
    private double _fps;

    private Metrics _lastMetrics = Metrics.Empty;
    private AudioFrame _lastAudio = AudioFrame.Silent;

    // true while we're pushing settings INTO the panel controls, so their change
    // events don't write back (or fire against un-init state during XAML load).
    private bool _loadingSettings = true;

    // reused HUD paint
    private readonly SKPaint _hudPaint = new()
    {
        IsAntialias = true,
        TextSize = 14f,
        Typeface = SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default
    };

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsService.Load();
        _collector = new DataCollector();
        _system = new ParticleSystem(_settings);
        _audio = new AudioAnalyzer();

        ApplySettingsToWindow();

        Loaded += (_, _) =>
        {
            _collector.Start();
            if (_settings.AudioReactive) _audio.Start();
            RefreshToolbar();
        };

        CompositionTarget.Rendering += OnRendering;
    }

    private void ApplySettingsToWindow()
    {
        Topmost = _settings.AlwaysOnTop;
        Width = _settings.WindowWidth;
        Height = _settings.WindowHeight;

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft;
            Top = _settings.WindowTop;
        }
    }

    // ------------------------------------------------------------- render loop

    private void OnRendering(object? sender, EventArgs e)
    {
        var rt = ((RenderingEventArgs)e).RenderingTime;
        if (rt == _lastRenderTime) return;          // compositor can fire twice per tick
        double dt = (rt - _lastRenderTime).TotalSeconds;
        _lastRenderTime = rt;

        if (dt <= 0) return;
        if (dt > 0.1) dt = 0.1;                      // clamp after a stall so nothing teleports

        // throttle to the target fps regardless of monitor refresh (144Hz-friendly).
        _accum += dt;
        double frame = 1.0 / Math.Max(15, _settings.TargetFps);
        if (_accum < frame) return;

        double step = _accum;
        _accum = 0;

        if (_collector.IsRunning)
        {
            _lastMetrics = _collector.GetLatest();
            _lastAudio = _settings.AudioReactive ? _audio.GetLatest() : AudioFrame.Silent;
            _system.SetAudio(_settings.AudioReactive, _lastAudio);

            var size = Canvas.CanvasSize;            // device pixels
            _system.Update((float)step, _lastMetrics, size.Width, size.Height);
            Canvas.InvalidateVisual();
        }

        // fps meter
        _frameCount++;
        _fpsAccum += step;
        if (_fpsAccum >= 0.5)
        {
            _fps = _frameCount / _fpsAccum;
            _frameCount = 0;
            _fpsAccum = 0;
        }
    }

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        _system.Draw(canvas);

        if (_settings.ShowDebug)
            DrawHud(canvas);
    }

    private void DrawHud(SKCanvas canvas)
    {
        var m = _lastMetrics;
        var lines = new List<string>
        {
            $"CPU  {m.CpuPercent,5:0.0} %",
            $"RAM  {m.AvailableRamMb,6:0} MB free  ({m.MemoryLoad * 100f,4:0} %)",
            $"NET  {m.NetworkKBps,7:0.0} KB/s",
            $"MOT  {_system.Count,5} particles",
            $"FPS  {_fps,5:0.0}",
        };

        if (_settings.AudioReactive)
        {
            var a = _lastAudio;
            string beat = a.Beat ? " *" : "";
            lines.Add($"AUD  L{a.Level:0.00} B{a.Bass:0.00} M{a.Mid:0.00} T{a.Treble:0.00}{beat}");
        }

        float x = 14f, y = 24f;
        foreach (var line in lines)
        {
            // shadow then text -> readable over any desktop.
            _hudPaint.Color = new SKColor(0, 0, 0, 170);
            canvas.DrawText(line, x + 1, y + 1, _hudPaint);
            _hudPaint.Color = new SKColor(0x9B, 0xF6, 0xFF, 235);
            canvas.DrawText(line, x, y, _hudPaint);
            y += 18f;
        }
    }

    // ------------------------------------------------------------- toolbar

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => ToggleCollection();
    private void BtnDebug_Click(object sender, RoutedEventArgs e) => ToggleDebug();
    private void BtnAudio_Click(object sender, RoutedEventArgs e) => ToggleAudio();
    private void BtnPin_Click(object sender, RoutedEventArgs e) => ToggleAlwaysOnTop();
    private void BtnGhost_Click(object sender, RoutedEventArgs e) => SetClickThrough(true);
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleAudio()
    {
        _settings.AudioReactive = !_settings.AudioReactive;
        if (_settings.AudioReactive) _audio.Start();
        else _audio.Stop();
        RefreshToolbar();
    }

    /// <summary>fire a butterfly burst on demand (handy for recording the demo gif).</summary>
    private void ManualBurst()
    {
        var size = Canvas.CanvasSize;
        _system.TriggerButterflyBurst(size.Width * 0.5f, size.Height * 0.5f);
    }

    private void ToggleCollection()
    {
        if (_collector.IsRunning) _collector.Stop();
        else _collector.Start();
        RefreshToolbar();
    }

    private void ToggleDebug()
    {
        _settings.ShowDebug = !_settings.ShowDebug;
        RefreshToolbar();
    }

    private void ToggleAlwaysOnTop()
    {
        _settings.AlwaysOnTop = !_settings.AlwaysOnTop;
        Topmost = _settings.AlwaysOnTop;
        RefreshToolbar();
    }

    private void RefreshToolbar()
    {
        BtnPlay.Content = _collector.IsRunning ? "\u23F8" : "\u25B6"; // pause / play
        BtnDebug.Opacity = _settings.ShowDebug ? 1.0 : 0.45;
        BtnAudio.Opacity = _settings.AudioReactive ? 1.0 : 0.45;
        BtnPin.Opacity = _settings.AlwaysOnTop ? 1.0 : 0.45;
        BtnSettings.Opacity = SettingsPanel.Visibility == Visibility.Visible ? 1.0 : 0.7;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // drag the whole overlay by grabbing anywhere on the canvas.
        if (e.ButtonState == MouseButtonState.Pressed && !_settings.ClickThrough)
        {
            try { DragMove(); } catch { /* ignore the rare re-entrancy throw */ }
        }
    }

    // ------------------------------------------------------------- settings panel

    private void BtnSettings_Click(object sender, RoutedEventArgs e) => ToggleSettings();

    private void ToggleSettings()
    {
        if (SettingsPanel.Visibility == Visibility.Visible) CloseSettings();
        else OpenSettings();
    }

    private void OpenSettings()
    {
        if (_settings.ClickThrough) SetClickThrough(false); // panel must be clickable
        PopulateSettings();
        SettingsPanel.Visibility = Visibility.Visible;
        RefreshToolbar();
    }

    private void CloseSettings()
    {
        SettingsPanel.Visibility = Visibility.Collapsed;
        SettingsService.Save(_settings); // persist on close
        RefreshToolbar();
    }

    private void BtnDone_Click(object sender, RoutedEventArgs e) => CloseSettings();

    /// <summary>push the current settings into the panel controls (without triggering write-back).</summary>
    private void PopulateSettings()
    {
        _loadingSettings = true;

        RbLow.IsChecked = _settings.Quality == QualityLevel.Low;
        RbMedium.IsChecked = _settings.Quality == QualityLevel.Medium;
        RbHigh.IsChecked = _settings.Quality == QualityLevel.High;

        ChkDebug.IsChecked = _settings.ShowDebug;
        ChkLines.IsChecked = _settings.ConstellationLines;
        ChkAudio.IsChecked = _settings.AudioReactive;
        ChkTop.IsChecked = _settings.AlwaysOnTop;

        SldEmit.Value = _settings.EmissionMultiplier;
        SldMax.Value = _settings.MaxParticles;
        SldFps.Value = _settings.TargetFps;

        UpdateSliderLabels();
        _loadingSettings = false;
    }

    private void UpdateSliderLabels()
    {
        LblEmit.Text = $"emission rate  ({_settings.EmissionMultiplier:0.0}\u00D7 per %cpu)";
        LblMax.Text = $"max particles  ({_settings.MaxParticles})";
        LblFps.Text = $"target fps  ({_settings.TargetFps})";
    }

    private void Quality_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        if (RbLow.IsChecked == true) _settings.Quality = QualityLevel.Low;
        else if (RbMedium.IsChecked == true) _settings.Quality = QualityLevel.Medium;
        else if (RbHigh.IsChecked == true) _settings.Quality = QualityLevel.High;
    }

    private void ChkDebug_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.ShowDebug = ChkDebug.IsChecked == true;
        RefreshToolbar();
    }

    private void ChkLines_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.ConstellationLines = ChkLines.IsChecked == true;
    }

    private void ChkAudio_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.AudioReactive = ChkAudio.IsChecked == true;
        if (_settings.AudioReactive) _audio.Start();
        else _audio.Stop();
        RefreshToolbar();
    }

    private void ChkTop_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        _settings.AlwaysOnTop = ChkTop.IsChecked == true;
        Topmost = _settings.AlwaysOnTop;
        RefreshToolbar();
    }

    private void SldEmit_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings) return;
        _settings.EmissionMultiplier = (float)Math.Round(e.NewValue, 1);
        UpdateSliderLabels();
    }

    private void SldMax_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings) return;
        _settings.MaxParticles = (int)Math.Round(e.NewValue / 100.0) * 100;
        UpdateSliderLabels();
    }

    private void SldFps_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loadingSettings) return;
        _settings.TargetFps = (int)Math.Round(e.NewValue);
        UpdateSliderLabels();
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        var d = new Settings(); // defaults

        // mutate the LIVE settings object in place (the particle system holds this same
        // reference), keeping window placement + click-through as they are.
        _settings.Quality = d.Quality;
        _settings.ShowDebug = d.ShowDebug;
        _settings.ConstellationLines = d.ConstellationLines;
        _settings.AudioReactive = d.AudioReactive;
        _settings.AlwaysOnTop = d.AlwaysOnTop;
        _settings.EmissionMultiplier = d.EmissionMultiplier;
        _settings.MaxParticles = d.MaxParticles;
        _settings.TargetFps = d.TargetFps;

        // apply side effects that aren't polled every frame
        Topmost = _settings.AlwaysOnTop;
        if (_settings.AudioReactive && !_audio.IsRunning) _audio.Start();
        else if (!_settings.AudioReactive && _audio.IsRunning) _audio.Stop();

        PopulateSettings();
        RefreshToolbar();
    }

    // clicking empty panel space shouldn't drag the window
    private void SettingsPanel_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ------------------------------------------------------------- click-through overlay

    private IntPtr _hwnd;

    private void SetClickThrough(bool on)
    {
        _settings.ClickThrough = on;
        if (_hwnd == IntPtr.Zero) return;

        int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
        if (on) ex |= WS_EX_TRANSPARENT;
        else ex &= ~WS_EX_TRANSPARENT;
        SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
    }

    // ------------------------------------------------------------- hotkeys + hwnd

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;

        var src = HwndSource.FromHwnd(_hwnd);
        src?.AddHook(WndProc);

        // Ctrl+Alt+<key>. these work even in click-through mode, which is the point.
        RegisterHotKey(_hwnd, Hk_Ghost, MOD_CONTROL | MOD_ALT, VK_S);
        RegisterHotKey(_hwnd, Hk_Debug, MOD_CONTROL | MOD_ALT, VK_D);
        RegisterHotKey(_hwnd, Hk_Play,  MOD_CONTROL | MOD_ALT, VK_SPACE);
        RegisterHotKey(_hwnd, Hk_Audio, MOD_CONTROL | MOD_ALT, VK_A);
        RegisterHotKey(_hwnd, Hk_Burst, MOD_CONTROL | MOD_ALT, VK_B);
        RegisterHotKey(_hwnd, Hk_Settings, MOD_CONTROL | MOD_ALT, VK_O);
        RegisterHotKey(_hwnd, Hk_Quit,  MOD_CONTROL | MOD_ALT, VK_Q);

        if (_settings.ClickThrough) SetClickThrough(true);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            switch (wParam.ToInt32())
            {
                case Hk_Ghost: SetClickThrough(!_settings.ClickThrough); handled = true; break;
                case Hk_Debug: ToggleDebug();      handled = true; break;
                case Hk_Play:  ToggleCollection(); handled = true; break;
                case Hk_Audio: ToggleAudio();      handled = true; break;
                case Hk_Burst: ManualBurst();      handled = true; break;
                case Hk_Settings: ToggleSettings(); handled = true; break;
                case Hk_Quit:  Close();            handled = true; break;
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;

        // remember placement
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        SettingsService.Save(_settings);

        if (_hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, Hk_Ghost);
            UnregisterHotKey(_hwnd, Hk_Debug);
            UnregisterHotKey(_hwnd, Hk_Play);
            UnregisterHotKey(_hwnd, Hk_Audio);
            UnregisterHotKey(_hwnd, Hk_Burst);
            UnregisterHotKey(_hwnd, Hk_Settings);
            UnregisterHotKey(_hwnd, Hk_Quit);
        }

        _collector.Dispose();
        _audio.Dispose();
        _system.Dispose();
        _hudPaint.Dispose();

        base.OnClosing(e);
    }

    // ------------------------------------------------------------- native interop

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint VK_S = 0x53, VK_D = 0x44, VK_Q = 0x51, VK_SPACE = 0x20, VK_A = 0x41, VK_B = 0x42, VK_O = 0x4F;

    private const int Hk_Ghost = 9001, Hk_Debug = 9002, Hk_Play = 9003,
                      Hk_Quit = 9004, Hk_Audio = 9005, Hk_Burst = 9006, Hk_Settings = 9007;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
