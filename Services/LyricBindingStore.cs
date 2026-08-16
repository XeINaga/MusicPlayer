using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicPlayer.Services;

/// <summary>
/// Persists manual associations between an audio file and a user-chosen lyric file
/// (e.g. an .lrc / .srt / .txt that does not share the audio's base name).
/// Stored as a flat JSON map: audio path -&gt; lyric path.
/// </summary>
public static class LyricBindingStore
{
    private static string FilePath =>
        Path.Combine(DataLocation.Root, "lyricbindings.json");

    private static Dictionary<string, string> _map = Load();

    private static Dictionary<string, string> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (dict != null)
                    return dict;
            }
        }
        catch
        {
            // ignore corrupt file
        }

        return new Dictionary<string, string>();
    }

    private static void Save()
    {
        try
        {
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_map, new JsonSerializerOptions { WriteIndented = true }), System.Text.Encoding.UTF8);
        }
        catch
        {
            // best-effort persistence
        }
    }

    public static string? Get(string audioPath) =>
        _map.TryGetValue(audioPath, out var v) ? v : null;

    public static void Set(string audioPath, string lyricPath)
    {
        _map[audioPath] = lyricPath;
        Save();
    }

    public static void Clear(string audioPath)
    {
        if (_map.Remove(audioPath))
            Save();
    }
}
