using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>
/// Persists application state so the player can resume like QQ Music:
///  - Auto playlist (local library):  %LOCALAPPDATA%\MusicPlayer\playlist.json
///  - Recently played:                %LOCALAPPDATA%\MusicPlayer\recent.json
///  - User playlists:                 %LOCALAPPDATA%\MusicPlayer\playlists.json
///  - Last playback progress:         %LOCALAPPDATA%\MusicPlayer\progress.json
///  - Manual export:                  standard .m3u / .m3u8 chosen by the user
/// </summary>
public sealed class PlaylistStore
{
    private static string AppDir => DataLocation.Root;
    private static readonly string PlaylistFile = Path.Combine(AppDir, "playlist.json");
    private static readonly string RecentFile = Path.Combine(AppDir, "recent.json");
    private static readonly string PlaylistsFile = Path.Combine(AppDir, "playlists.json");
    private static readonly string ProgressFile = Path.Combine(AppDir, "progress.json");

    private static void EnsureDir()
    {
        try { Directory.CreateDirectory(AppDir); }
        catch { /* best-effort */ }
    }

    // ---------- Local library (auto playlist) ----------

    public static void SaveAutoPlaylist(IEnumerable<string> paths)
    {
        try
        {
            EnsureDir();
            var data = new PlaylistData { Paths = new List<string>(paths) };
            File.WriteAllText(PlaylistFile, JsonSerializer.Serialize(data), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    public static List<string> LoadAutoPlaylist()
    {
        try
        {
            if (!File.Exists(PlaylistFile))
                return new List<string>();
            var data = JsonSerializer.Deserialize<PlaylistData>(File.ReadAllText(PlaylistFile, Encoding.UTF8));
            return data?.Paths ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    // ---------- Recently played ----------

    public static void SaveRecent(IEnumerable<string> paths)
    {
        try
        {
            EnsureDir();
            var data = new RecentData { Paths = new List<string>(paths) };
            File.WriteAllText(RecentFile, JsonSerializer.Serialize(data), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    public static List<string> LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentFile))
                return new List<string>();
            var data = JsonSerializer.Deserialize<RecentData>(File.ReadAllText(RecentFile, Encoding.UTF8));
            return data?.Paths ?? new List<string>();
        }
        catch { return new List<string>(); }
    }

    // ---------- User playlists ----------

    public static void SavePlaylists(IEnumerable<PlaylistDto> lists)
    {
        try
        {
            EnsureDir();
            var data = new PlaylistsData { Items = new List<PlaylistDto>(lists) };
            File.WriteAllText(PlaylistsFile, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    public static List<PlaylistDto> LoadPlaylists()
    {
        try
        {
            if (!File.Exists(PlaylistsFile))
                return new List<PlaylistDto>();
            var data = JsonSerializer.Deserialize<PlaylistsData>(File.ReadAllText(PlaylistsFile, Encoding.UTF8));
            return data?.Items ?? new List<PlaylistDto>();
        }
        catch { return new List<PlaylistDto>(); }
    }

    // ---------- Last playback progress ----------

    public static void SaveProgress(int index, TimeSpan position, string? path)
    {
        try
        {
            EnsureDir();
            var data = new ProgressData { Index = index, PositionMs = (long)position.TotalMilliseconds, Path = path };
            File.WriteAllText(ProgressFile, JsonSerializer.Serialize(data), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    public static ProgressData LoadProgress()
    {
        try
        {
            if (!File.Exists(ProgressFile))
                return new ProgressData { Index = -1 };
            var data = JsonSerializer.Deserialize<ProgressData>(File.ReadAllText(ProgressFile, Encoding.UTF8));
            return data ?? new ProgressData { Index = -1 };
        }
        catch { return new ProgressData { Index = -1 }; }
    }

    // ---------- Manual M3U export / import ----------

    public static void ExportM3U(string filePath, IList<Track> tracks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var t in tracks)
        {
            sb.AppendLine($"#EXTINF:-1,{t.Artist} - {t.Title}");
            sb.AppendLine(t.Path);
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    public static List<string> ImportM3U(string filePath)
    {
        var result = new List<string>();
        if (!File.Exists(filePath))
            return result;

        foreach (var raw in File.ReadLines(filePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;
            if (File.Exists(line))
                result.Add(line);
        }

        return result;
    }
}

public sealed class PlaylistData
{
    public List<string>? Paths { get; set; }
}

public sealed class RecentData
{
    public List<string>? Paths { get; set; }
}

public sealed class PlaylistDto
{
    public string Name { get; set; } = "新建歌单";
    public List<string>? Paths { get; set; }
}

public sealed class PlaylistsData
{
    public List<PlaylistDto>? Items { get; set; }
}

public sealed class ProgressData
{
    public int Index { get; set; } = -1;
    public long PositionMs { get; set; }
    public string? Path { get; set; }
}
