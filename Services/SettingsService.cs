using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SomniumWeaver.Services;

public enum QualityLevel { Low, Medium, High }

public enum AnomalyEngineKind { ZScore, Autoencoder }

/// <summary>everything the user can tweak, serialized to json in %AppData%.</summary>
public sealed class Settings
{
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowDebug { get; set; } = true;
    public bool ClickThrough { get; set; } = false;
    public bool ConstellationLines { get; set; } = true;
    public bool AudioReactive { get; set; } = false;
    public bool EnableHardwareSensors { get; set; } = true;  // LibreHardwareMonitor (gpu/temps)
    public bool WallpaperMode { get; set; } = false;          // render behind desktop icons
    public float AnomalyThreshold { get; set; } = 3.0f;       // z-score sigma to trigger a burst
    public AnomalyEngineKind AnomalyEngine { get; set; } = AnomalyEngineKind.ZScore;
    public bool LogTelemetryCsv { get; set; } = false;        // append vitals to a csv (for retraining)
    public QualityLevel Quality { get; set; } = QualityLevel.Medium;

    public int MaxParticles { get; set; } = 5000;
    public float EmissionMultiplier { get; set; } = 2.0f; // particles/sec per %cpu
    public int TargetFps { get; set; } = 60;

    // remembered window placement
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 900;
    public double WindowHeight { get; set; } = 600;
}

public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SomniumWeaver");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } // "Quality": "High" instead of a number
    };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var s = JsonSerializer.Deserialize<Settings>(json, JsonOpts);
                if (s != null) return s;
            }
        }
        catch { /* fall through to defaults */ }
        return new Settings();
    }

    public static void Save(Settings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, JsonOpts));
        }
        catch { /* not worth crashing over */ }
    }
}
