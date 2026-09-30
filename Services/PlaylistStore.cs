using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    // Properties (not static readonly fields!) so a runtime change of the data
    // directory (settings page) is reflected immediately instead of being
    // baked in on first access and silently writing to the OLD directory.
    private static string AppDir => DataLocation.Root;
    private static string PlaylistFile => Path.Combine(AppDir, "playlist.json");
    private static string RecentFile => Path.Combine(AppDir, "recent.json");
    private static string PlaylistsFile => Path.Combine(AppDir, "playlists.json");
    private static string ProgressFile => Path.Combine(AppDir, "progress.json");

    private static void EnsureDir()
    {
        try { Directory.CreateDirectory(AppDir); }
        catch (Exception ex)
        {
            AppLog.WritePersistenceFailure(PlaylistFile, ex);
        }
    }

    // ---------- Local library (auto playlist) ----------

    public static void SaveAutoPlaylist(IEnumerable<Track> tracks)
    {
        try
        {
            EnsureDir();
            var items = tracks.Select(t => new TrackEntry
            {
                Path = t.Path,
                DateAdded = t.DateAdded,
                PlayCount = t.PlayCount,
                Favorite = t.Favorite
            }).ToList();
            var data = new PlaylistData { Items = items };
            AtomicFile.WriteAllText(PlaylistFile, JsonSerializer.Serialize(data), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    public static List<TrackEntry> LoadAutoPlaylist()
    {
        try
        {
            if (!File.Exists(PlaylistFile))
                return new List<TrackEntry>();
            var raw = File.ReadAllText(PlaylistFile, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<PlaylistData>(raw);
            if (data == null)
                return new List<TrackEntry>();

            // v2 format: Items list present
            if (data.Items != null && data.Items.Count > 0)
                return data.Items;

            // v1 backward compat: only Paths string array
            if (data.Paths != null && data.Paths.Count > 0)
            {
                var migrated = data.Paths.Select(p => new TrackEntry
                {
                    Path = p,
                    DateAdded = DateTime.Now
                }).ToList();

                // Persist the migrated v2 format so we only migrate once.
                try
                {
                    var v2 = new PlaylistData { Items = migrated };
                    AtomicFile.WriteAllText(PlaylistFile, JsonSerializer.Serialize(v2), Encoding.UTF8);
                }
                catch { /* best-effort */ }

                return migrated;
            }

            return new List<TrackEntry>();
        }
        catch (Exception ex)
        {
            BackupCorruptFile(PlaylistFile, ex);
            return new List<TrackEntry>();
        }
    }

    // ---------- Random round (shuffle bag) ----------

    private static string RandomBagFile => Path.Combine(DataLocation.Root, "randombag.json");

    public static void SaveRandomBag(IEnumerable<string> paths)
    {
        try
        {
            EnsureDir();
            var data = new RandomBagData { Paths = paths.ToList() };
            AtomicFile.WriteAllText(RandomBagFile, JsonSerializer.Serialize(data), Encoding.UTF8);
        }
        catch { /* best-effort */ }
    }

    public static List<string> LoadRandomBag()
    {
        try
        {
            if (!File.Exists(RandomBagFile))
                return new List<string>();
            var data = JsonSerializer.Deserialize<RandomBagData>(File.ReadAllText(RandomBagFile, Encoding.UTF8));
            return data?.Paths ?? new List<string>();
        }
        catch (Exception ex)
        {
            AppLog.WritePersistenceFailure(RandomBagFile, ex);
            return new List<string>();
        }
    }

    public static void ClearRandomBag()
    {
        try { if (File.Exists(RandomBagFile)) File.Delete(RandomBagFile); } catch { }
    }

    // ---------- Recently played ----------

    public static void SaveRecent(IEnumerable<Track> tracks)
    {
        try
        {
            EnsureDir();
            var items = tracks.Select(t => new TrackEntry
            {
                Path = t.Path,
                DateAdded = t.DateAdded,
                PlayCount = t.PlayCount,
                Favorite = t.Favorite
            }).ToList();
            var data = new RecentData { Items = items };
            AtomicFile.WriteAllText(RecentFile, JsonSerializer.Serialize(data), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLog.WritePersistenceFailure(RecentFile, ex);
        }
    }

    public static List<TrackEntry> LoadRecent()
    {
        try
        {
            if (!File.Exists(RecentFile))
                return new List<TrackEntry>();
            var raw = File.ReadAllText(RecentFile, Encoding.UTF8);
            var data = JsonSerializer.Deserialize<RecentData>(raw);
            if (data == null)
                return new List<TrackEntry>();

            // v2 format: Items list present
            if (data.Items != null && data.Items.Count > 0)
                return data.Items;

            // v1 backward compat: only Paths string array
            if (data.Paths != null && data.Paths.Count > 0)
            {
                var migrated = data.Paths.Select(p => new TrackEntry
                {
                    Path = p,
                    DateAdded = DateTime.Now
                }).ToList();

                try
                {
                    var v2 = new RecentData { Items = migrated };
                    AtomicFile.WriteAllText(RecentFile, JsonSerializer.Serialize(v2), Encoding.UTF8);
                }
                catch { /* best-effort */ }

                return migrated;
            }

            return new List<TrackEntry>();
        }
        catch (Exception ex)
        {
            BackupCorruptFile(RecentFile, ex);
            return new List<TrackEntry>();
        }
    }

    // ---------- User playlists ----------

    public static void SavePlaylists(IEnumerable<PlaylistDto> lists)
    {
        try
        {
            EnsureDir();
            var data = new PlaylistsData { Items = new List<PlaylistDto>(lists) };
            AtomicFile.WriteAllText(PlaylistsFile, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLog.WritePersistenceFailure(PlaylistsFile, ex);
        }
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
        catch (Exception ex)
        {
            BackupCorruptFile(PlaylistsFile, ex);
            return new List<PlaylistDto>();
        }
    }

    // ---------- Last playback progress ----------

    public static void SaveProgress(int index, TimeSpan position, string? path)
    {
        try
        {
            EnsureDir();
            var data = new ProgressData { Index = index, PositionMs = (long)position.TotalMilliseconds, Path = path };
            AtomicFile.WriteAllText(ProgressFile, JsonSerializer.Serialize(data), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            AppLog.WritePersistenceFailure(ProgressFile, ex);
        }
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
        catch (Exception ex)
        {
            BackupCorruptFile(ProgressFile, ex);
            return new ProgressData { Index = -1 };
        }
    }

    private static void BackupCorruptFile(string path, Exception exception)
    {
        AppLog.WritePersistenceFailure(path, exception);
        try
        {
            if (File.Exists(path))
            {
                var backup = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
                File.Copy(path, backup);
            }
        }
        catch (Exception backupException)
        {
            AppLog.WritePersistenceFailure(path + " (backup)", backupException);
        }
    }

    // ---------- Manual M3U export / import ----------

    public static void ExportM3U(string filePath, IList<Track> tracks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var t in tracks)
        {
            // Write the real duration (seconds) instead of a fixed -1.
            var secs = Math.Max(0, (int)t.Duration.TotalSeconds);
            sb.AppendLine($"#EXTINF:{secs},{t.Artist} - {t.Title}");
            sb.AppendLine(t.Path);
        }

        File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
    }

    public static List<string> ImportM3U(string filePath)
    {
        var result = new List<string>();
        if (!File.Exists(filePath))
            return result;

        // Detect the file's encoding: ANSI/GBK m3u files with Chinese paths
        // decoded as UTF-8 silently dropped every entry.
        string[] lines;
        try
        {
            var text = EncodingHelper.ReadText(filePath, "auto");
            lines = text.Replace("\r\n", "\n").Split('\n');
        }
        catch
        {
            return result;
        }

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath))!;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            // Relative paths resolve against the m3u's own directory (M3U spec),
            // not the process working directory.
            var candidate = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(baseDir, line));
            if (File.Exists(candidate))
                result.Add(candidate);
        }

        return result;
    }
}

// Persisted random-round state: the tracks still pending in the shuffle bag,
// so "every track once before any repeat" survives an app restart.
public sealed class RandomBagData
{
    public List<string> Paths { get; set; } = new();
}

public sealed class TrackEntry
{
    public string Path { get; set; } = "";
    public DateTime DateAdded { get; set; } = DateTime.Now;
    public int PlayCount { get; set; }
    public bool Favorite { get; set; }
}

public sealed class PlaylistData
{
    public List<string>? Paths { get; set; }   // v1 backward compat
    public List<TrackEntry>? Items { get; set; } // v2
}

public sealed class RecentData
{
    public List<string>? Paths { get; set; }   // v1 backward compat
    public List<TrackEntry>? Items { get; set; } // v2
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
