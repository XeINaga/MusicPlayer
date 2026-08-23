using System;
using System.IO;

namespace MusicPlayer.Services;

/// <summary>
/// Central, runtime-configurable root directory for all persisted application
/// data (auto-playlist, recent list, user playlists, progress, lyric bindings).
///
/// The *config* file (settings.json) always lives in the default
/// %LOCALAPPDATA%\MusicPlayer so the app can always boot and read the chosen
/// location; only the bulk data/cache moves when the user changes CacheDir.
/// </summary>
public static class DataLocation
{
    /// <summary>Default data/cache root: %LOCALAPPDATA%\MusicPlayer.</summary>
    public static readonly string DefaultRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicPlayer");

    private static string _root = DefaultRoot;

    /// <summary>Current data/cache root directory.</summary>
    public static string Root => _root;

    /// <summary>True when the user has overridden the default location.</summary>
    public static bool IsCustom => !string.Equals(_root, DefaultRoot, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Apply a custom data/cache directory. Empty/whitespace keeps the default.
    /// Trailing separators are trimmed. The directory is NOT created here.
    /// </summary>
    public static void Apply(string? customDir)
    {
        if (string.IsNullOrWhiteSpace(customDir))
            _root = DefaultRoot;
        else
            _root = customDir.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    /// <summary>Ensure the current root directory exists.</summary>
    public static void EnsureRoot()
    {
        try { Directory.CreateDirectory(_root); } catch { /* best-effort */ }
    }
}
