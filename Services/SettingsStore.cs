using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MusicPlayer.Services;

/// <summary>
/// Application settings, primarily the desktop-lyrics visual style and the
/// default playback mode. Persisted to %LOCALAPPDATA%\MusicPlayer\settings.json.
/// </summary>
public sealed class AppSettings
{
    public double LyricFontSize { get; set; } = 24;
    public string LyricColor { get; set; } = "#FFFFFFFF";
    public string AccentColor { get; set; } = "#31c27c"; // app theme / accent color

    /// <summary>Custom data/cache directory. Empty = default %LOCALAPPDATA%\MusicPlayer.</summary>
    public string CacheDir { get; set; } = "";
    public double LyricBgOpacity { get; set; } = 0.0; // 0..1 (0 = fully transparent)
    public bool LyricBold { get; set; }
    public string LyricAlign { get; set; } = "Center"; // "Left" | "Center"
    public bool LyricClickThroughDefault { get; set; }
    public string DefaultPlayMode { get; set; } = "Sequential"; // PlayMode name

    // Player state persisted across launches.
    public double Volume { get; set; } = 0.8;          // 0..1
    public bool CoverSpin { get; set; } = true;        // rotate the vinyl while playing
    public string ViewMode { get; set; } = "Grid";     // "Grid" | "List"
    public string SortBy { get; set; } = "Default";    // Default|Title|Artist|Album|DateAdded|Duration

    // Last window geometry (px). X/Y < 0 means "use centered default".
    public int WindowW { get; set; } = 1380;
    public int WindowH { get; set; } = 860;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;
}

public sealed class SettingsStore
{
    // settings.json stays in the DEFAULT root so the app can always boot and
    // read the chosen CacheDir; only the bulk data follows DataLocation.Root.
    private static readonly string Dir = DataLocation.DefaultRoot;
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
                if (data != null)
                    return data;
            }
        }
        catch
        {
            // fall through to defaults
        }

        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch
        {
            // best-effort
        }
    }
}
