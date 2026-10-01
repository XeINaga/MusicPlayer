using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;

namespace MusicPlayer.Services;

/// <summary>
/// Persistent cache of audio-tag metadata (title / artist / album / duration),
/// keyed by file path with the file's last-write time as the validity check.
///
/// The library rebuilds its metadata from the audio files on every launch —
/// with a few thousand tracks that means 1-3 seconds of background TagLib
/// reads (only the cover bitmap had a disk cache before). With this cache a
/// warm start re-reads nothing: entries whose file mtime is unchanged are
/// served straight from the JSON, so only new or modified files are scanned.
///
/// The JSON lives next to the other data files (~250 KB for 1500 tracks) and
/// is written atomically, debounced 2 s after the last Put (Put is called
/// from background scan threads, so no DispatcherTimer here — a plain
/// System.Threading.Timer + lock instead).
/// </summary>
internal static class MetadataCache
{
    private sealed record Entry(
        long MtimeTicks,
        string? Title,
        string? Artist,
        string? Album,
        int DurationMs);

    private static readonly object Gate = new();
    private static Dictionary<string, Entry>? _entries; // key: full path, case-insensitive
    private static bool _dirty;
    private static Timer? _saveTimer;

    private static string CacheFile => Path.Combine(DataLocation.Root, "metacache.json");

    /// <summary>Resolve a track's metadata from cache. False when the path is
    /// unknown or the file changed since it was cached.</summary>
    public static bool TryGet(string path, long mtimeTicks,
        out string? title, out string? artist, out string? album, out TimeSpan duration)
    {
        title = artist = album = null;
        duration = TimeSpan.Zero;

        if (mtimeTicks == 0)
            return false;

        lock (Gate)
        {
            if (_entries == null)
                Load();

            if (_entries != null &&
                _entries.TryGetValue(path, out var e) &&
                e.MtimeTicks == mtimeTicks)
            {
                title = e.Title;
                artist = e.Artist;
                album = e.Album;
                duration = TimeSpan.FromMilliseconds(e.DurationMs);
                return true;
            }
        }
        return false;
    }

    /// <summary>Store a freshly-scanned entry and schedule a debounced save.</summary>
    public static void Put(string path, long mtimeTicks,
        string? title, string? artist, string? album, TimeSpan duration)
    {
        if (mtimeTicks == 0)
            return;

        lock (Gate)
        {
            if (_entries == null)
                Load();

            _entries![path] = new Entry(
                mtimeTicks, title, artist, album,
                (int)Math.Clamp(duration.TotalMilliseconds, 0, int.MaxValue));

            if (!_dirty)
            {
                _dirty = true;
                _saveTimer ??= new Timer(_ => Flush(), null, 2000, Timeout.Infinite);
                _saveTimer.Change(2000, Timeout.Infinite);
            }
        }
    }

    private static void Flush()
    {
        string json;
        lock (Gate)
        {
            if (!_dirty || _entries == null)
                return;
            json = JsonSerializer.Serialize(_entries,
                new JsonSerializerOptions { WriteIndented = false });
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(DataLocation.Root);
            AtomicFile.WriteAllText(CacheFile, json, System.Text.Encoding.UTF8);
        }
        catch
        {
            // best effort — a failed cache write only costs scan time next launch
        }
    }

    private static void Load()
    {
        _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(CacheFile))
                return;

            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(
                File.ReadAllText(CacheFile, System.Text.Encoding.UTF8));
            if (data == null)
                return;

            foreach (var (path, entry) in data)
            {
                // Skip malformed rows instead of rejecting the whole cache.
                if (!string.IsNullOrEmpty(path) && entry is { MtimeTicks: > 0 })
                    _entries[path] = entry;
            }
        }
        catch
        {
            // Corrupt cache — start empty, it rebuilds from the files.
            _entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
