using System;
using System.Collections.Generic;
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

    /// <summary>
    /// Colour theme: "Dark" | "Light". Applied to RootGrid.RequestedTheme, which
    /// is what makes every {ThemeResource} in Themes/SukiTheme.xaml re-evaluate.
    /// </summary>
    public string ThemeMode { get; set; } = "Dark";

    /// <summary>Custom data/cache directory. Empty = default %LOCALAPPDATA%\MusicPlayer.</summary>
    public string CacheDir { get; set; } = "";
    public double LyricBgOpacity { get; set; } = 0.0; // 0..1 (0 = fully transparent)
    public bool LyricBold { get; set; }
    public string LyricAlign { get; set; } = "Center"; // "Left" | "Center"
    public bool LyricClickThroughDefault { get; set; }
    /// <summary>Forced lyrics-file encoding: "auto" | "gbk" | "shift_jis" | "big5" | "utf-8".</summary>
    public string LyricEncoding { get; set; } = "auto";
    /// <summary>User lyric calibration: positive = lyrics appear LATER (ms).
    /// (The [offset:] tag inside an LRC file is applied at parse time on top of this.)</summary>
    public int LyricOffsetMs { get; set; }
    /// <summary>Show romaji / translation lines in the lyrics panel AND the desktop overlay.</summary>
    public bool LyricShowRomaji { get; set; } = true;
    public bool LyricShowTranslation { get; set; } = true;

    /// <summary>
    /// Preferred lyric source when downloading lyrics.
    /// "Auto" (try NetEase → QQ → LRCLIB, first hit wins)
    /// | "NetEase" | "QQ" | "LRCLIB".
    /// When a specific source is chosen it is used exclusively (no fallback),
    /// so the user stays in control of where lyrics come from.
    /// </summary>
    public string LyricSource { get; set; } = "Auto";

    /// <summary>
    /// What the one-click "补全歌词" action fills in:
    /// "All" (main lyric + translation + romaji, whichever the source has)
    /// | "MainOnly" (original lyric only)
    /// | "ExtrasOnly" (only add translation/romaji; never overwrite the main lyric).
    /// </summary>
    public string LyricFillMode { get; set; } = "All";
    /// <summary>Desktop-lyrics overlay window position; -1,-1 = default (bottom center).</summary>
    public int LyricPosX { get; set; } = -1;
    public int LyricPosY { get; set; } = -1;
    public string DefaultPlayMode { get; set; } = "Sequential"; // PlayMode name

    /// <summary>What the window close button does: "Exit" or "Tray" (minimize to tray).</summary>
    public string CloseAction { get; set; } = "Exit";

    /// <summary>
    /// Dynamic volume: route all playback through FFmpeg's loudnorm filter so
    /// quiet songs are amplified and loud ones attenuated (EBU R128, target
    /// -16 LUFS). Takes effect on the current track via a seamless reload.
    /// </summary>
    public bool DynamicVolume { get; set; }

    // Player state persisted across launches.
    public double Volume { get; set; } = 0.8;          // 0..1
    public double PlaybackRate { get; set; } = 1.0;     // 0.5–2.0
    public bool CoverSpin { get; set; } = true;        // rotate the vinyl while playing
    public string ViewMode { get; set; } = "Grid";     // "Grid" | "List"
    public string SortBy { get; set; } = "Default";    // Default|Title|Artist|Album|DateAdded|Duration
    /// <summary>Crossfade duration in ms: 0 = off, 1000, 2000, 3000.</summary>
    public int CrossfadeDurationMs { get; set; }

    // Global hotkeys.
    public bool UseGlobalHotkeys { get; set; }

    /// <summary>Mirrors the auto-start registry state (registry is the source of truth on launch).</summary>
    public bool AutoStart { get; set; }

    // Last window geometry (px). X/Y < 0 means "use centered default".
    public int WindowW { get; set; } = 1380;
    public int WindowH { get; set; } = 860;
    public int WindowX { get; set; } = -1;
    public int WindowY { get; set; } = -1;

    // Last.fm scrobbling.
    public string LastFmApiKey { get; set; } = "";
    public string LastFmApiSecret { get; set; } = "";
    public string LastFmSessionKey { get; set; } = "";
    public string LastFmUsername { get; set; } = "";

    /// <summary>
    /// Folders under library surveillance. Every launch rescans them and syncs
    /// the result into the library: new audio files are added, files that
    /// disappeared are removed.
    /// </summary>
    public List<WatchedFolder> WatchedFolders { get; set; } = new();

    /// <summary>
    /// Track paths the user deliberately removed from the library while the
    /// file still exists inside a watched folder. The startup sync never
    /// re-adds these; adding the file back by hand clears the exclusion.
    /// </summary>
    public List<string> LibraryExclusions { get; set; } = new();
}

/// <summary>One monitored folder and how deep the scan goes.</summary>
public sealed class WatchedFolder
{
    public string Path { get; set; } = "";
    public bool Recursive { get; set; }
}

/// <summary>Lyric sources the user can choose from.</summary>
public enum LyricSourceKind
{
    /// <summary>NetEase → QQ → LRCLIB, first hit wins.</summary>
    Auto,
    NetEase,
    QQ,
    LRCLIB,
}

/// <summary>What the one-click "补全歌词" action is allowed to write.</summary>
public enum LyricFillModeKind
{
    /// <summary>Original + translation + romaji, whichever the source provides.</summary>
    All,
    /// <summary>Only the original lyric; leave translation/romaji untouched.</summary>
    MainOnly,
    /// <summary>Only add translation/romaji; never overwrite an existing main lyric.</summary>
    ExtrasOnly,
}

/// <summary>Helpers mapping the persisted string settings to their enums.</summary>
public static class LyricPreferences
{
    public static LyricSourceKind ParseSource(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "netease" => LyricSourceKind.NetEase,
        "qq" => LyricSourceKind.QQ,
        "lrclib" => LyricSourceKind.LRCLIB,
        _ => LyricSourceKind.Auto,
    };

    public static LyricFillModeKind ParseFillMode(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "mainonly" => LyricFillModeKind.MainOnly,
        "extrasonly" => LyricFillModeKind.ExtrasOnly,
        _ => LyricFillModeKind.All,
    };

    public static string ToSetting(LyricSourceKind kind) => kind switch
    {
        LyricSourceKind.NetEase => "NetEase",
        LyricSourceKind.QQ => "QQ",
        LyricSourceKind.LRCLIB => "LRCLIB",
        _ => "Auto",
    };

    public static string ToSetting(LyricFillModeKind kind) => kind switch
    {
        LyricFillModeKind.MainOnly => "MainOnly",
        LyricFillModeKind.ExtrasOnly => "ExtrasOnly",
        _ => "All",
    };
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
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch
        {
            // best-effort
        }
    }
}
