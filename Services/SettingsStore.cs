using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
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
    public string AccentColor { get; set; } = "#ef4444"; // app theme / accent color (EchoMusic crimson)

    /// <summary>
    /// Colour theme: "Dark" | "Light". Applied to RootGrid.RequestedTheme, which
    /// is what makes every {ThemeResource} in Themes/SukiTheme.xaml re-evaluate.
    /// </summary>
    public string ThemeMode { get; set; } = "Light";

    /// <summary>
    /// One-shot migration flag: pre-EchoMusic installs stored the old defaults
    /// (Dark + QQ green). On first load after the UI rework they are switched
    /// to the EchoMusic look once; anything the user customised afterwards is
    /// left alone.
    /// </summary>
    public bool UiEchoMigrated { get; set; }

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
    /// Vertical order of the three lyric lines, as a permutation of the letters
    /// O(原文) R(罗马音) T(翻译) — e.g. "OTR" = original, translation, romaji.
    /// Applied to the in-app lyrics panel and the desktop overlay alike.
    /// Invalid values fall back to "ORT".
    /// </summary>
    public string LyricLineOrder { get; set; } = "ORT";

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
    /// <summary>True once a real lyric-window position has been persisted;
    /// negative X/Y on monitors left of the primary are legitimate, not "unset".</summary>
    public bool LyricPosSaved { get; set; }

    /// <summary>Auto-open the desktop lyrics on launch when they were on at exit.</summary>
    public bool LyricOverlayEnabled { get; set; }

    /// <summary>Desktop lyrics render as vertical character columns.</summary>
    public bool LyricVertical { get; set; }

    /// <summary>Desktop lyrics wrap width (logical px); 0 = auto-fit.</summary>
    public int LyricBoxWidth { get; set; }

    /// <summary>Karaoke colors: the sweep paints the sung prefix in
    /// LyricSungColor over the LyricUnsungColor base. Shared by the in-app
    /// lyrics panel and the desktop overlay.</summary>
    public string LyricSungColor { get; set; } = "#31C27C";
    public string LyricUnsungColor { get; set; } = "#FFFFFF";

    /// <summary>
    /// Prefer WORD-TIMED lyrics when downloading (QQ QRC / KuGou KRC): the
    /// saved file keeps per-word timing and the lyrics panel highlights the
    /// current syllable karaoke-style. Plain LRC stays the fallback when a
    /// source has no word-timed version.
    /// </summary>
    public bool LyricWordLyrics { get; set; }
    public string DefaultPlayMode { get; set; } = "Sequential"; // PlayMode name

    /// <summary>What the window close button does: "Exit" or "Tray" (minimize to tray).</summary>
    public string CloseAction { get; set; } = "Exit";

    /// <summary>
    /// Apply cached ReplayGain metadata as a fixed per-track volume adjustment.
    /// Tracks without ReplayGain metadata play at the user's selected volume.
    /// </summary>
    public bool DynamicVolume { get; set; }

    /// Legacy settings retained for backwards-compatible settings.json reads.
    /// They are no longer used by the fixed-gain implementation.
    public double DynNormPeak { get; set; } = 0.95;

    public double DynNormMaxGain { get; set; } = 10.0;

    public int DynNormWindow { get; set; } = 31;

    // ---------- 音效（图形均衡器 + 效果旋钮，镜像 SoundFx 的持久化层） ----------

    /// <summary>Sound effects master switch. When off, tracks load through the
    /// system decoder and no FFmpeg audio filter chain is built.</summary>
    public bool SoundEffectsEnabled { get; set; }

    /// <summary>Ten EQ band gains in dB (-12..+12), centers 31Hz..16kHz.</summary>
    public List<double> EqGains { get; set; } = new() { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>Last used EQ preset name ("Off".."Vocal", or "Custom").</summary>
    public string EqPreset { get; set; } = "Off";

    /// <summary>Effect knobs, 0..100 (Balance -100..100): high-frequency
    /// detail / aecho reverb / stereowiden / bass shelf / compressor punch /
    /// output channel balance.</summary>
    public double EffectHiFi { get; set; }
    public double EffectReverb { get; set; }
    public double EffectSurround { get; set; }
    public double EffectBass { get; set; }
    public double EffectPunch { get; set; }
    public double EffectBalance { get; set; }

    /// <summary>Sound-effect window client size (resizable; remembered).</summary>
    public int SoundFxW { get; set; } = 720;
    public int SoundFxH { get; set; } = 660;

    /// <summary>
    /// Show the audio FILE NAME's title instead of the embedded tag title in
    /// track lists (useful when tags are wrong, e.g. katakana conversions).
    /// </summary>
    public bool TitlePreferFilename { get; set; }

    /// <summary>
    /// Optional y.qq.com login cookie (uin + qm_keyst / qqmusic_key…). QQ
    /// closed keyword search and the plain LRC endpoint to logged-out clients;
    /// with a cookie pasted here the QQ lyric source works again.
    /// </summary>
    public string QqCookie { get; set; } = "";

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
    /// <summary>True once a real window position has been persisted; a
    /// negative X/Y on a secondary monitor is legitimate, not "unset".</summary>
    public bool WindowPosSaved { get; set; }

    /// <summary>Online lyric search window size (resizable; remembered).</summary>
    public int LyricSearchW { get; set; } = 560;
    public int LyricSearchH { get; set; } = 640;

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
    /// <summary>NetEase → QQ → KuGou → LRCLIB, first hit wins.</summary>
    Auto,
    NetEase,
    QQ,
    KuGou,
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
    /// <summary>Validate a lyric line order string: must be a permutation of
    /// O/R/T; anything else falls back to the default "ORT".</summary>
    public static string ParseLineOrder(string? raw)
    {
        var s = (raw ?? "").Trim().ToUpperInvariant();
        if (s.Length == 3 && s.Contains('O') && s.Contains('R') && s.Contains('T')
            && s.Distinct().Count() == 3)
            return s;
        return "ORT";
    }

    public static LyricSourceKind ParseSource(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "netease" => LyricSourceKind.NetEase,
        "qq" => LyricSourceKind.QQ,
        "kugou" => LyricSourceKind.KuGou,
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
        LyricSourceKind.KuGou => "Kugou",
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
                {
                    // The QQ login cookie is stored DPAPI-encrypted (Current
                    // User scope). Plaintext values from older versions are
                    // transparently migrated on the next Save.
                    data.QqCookie = Unprotect(data.QqCookie);
                    MigrateToEchoLook(data);
                    return data;
                }
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
            // Encrypt the QQ cookie at rest: swap in the protected value for
            // serialization, restore the plaintext on the live instance.
            var plainCookie = settings.QqCookie;
            var live = settings;
            try
            {
                live.QqCookie = string.IsNullOrEmpty(plainCookie) ? "" : Protect(plainCookie);
                AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(live, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            }
            finally
            {
                live.QqCookie = plainCookie;
            }
        }
        catch
        {
            // best-effort
        }
    }

    private const string CookiePrefix = "dpapi:";

    /// <summary>
    /// One-time switch to the EchoMusic look for installs created before the
    /// UI rework: theme to Light, and the accent to the new default crimson —
    /// but only when it still holds the old QQ-green default (a colour the
    /// user picked deliberately is never touched).
    /// </summary>
    private static void MigrateToEchoLook(AppSettings data)
    {
        if (data.UiEchoMigrated)
            return;
        data.UiEchoMigrated = true;
        data.ThemeMode = "Light";
        var accent = (data.AccentColor ?? "").Trim();
        if (accent.Length == 0 || accent.Equals("#31c27c", StringComparison.OrdinalIgnoreCase))
            data.AccentColor = "#ef4444";
        try { Save(data); } catch { /* best effort */ }
    }

    private static string Protect(string plain)
    {
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), null, DataProtectionScope.CurrentUser);
        return CookiePrefix + Convert.ToBase64String(blob);
    }

    private static string Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored) || !stored.StartsWith(CookiePrefix, StringComparison.Ordinal))
            return stored ?? ""; // plaintext from an older version — migrated on save
        try
        {
            var blob = Convert.FromBase64String(stored[CookiePrefix.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, null, DataProtectionScope.CurrentUser));
        }
        catch
        {
            return ""; // undecryptable (different user profile) — start clean
        }
    }
}
