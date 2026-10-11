using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading.Tasks;
using TagLib;

namespace MusicPlayer.Services;

/// <summary>
/// Reads embedded ReplayGain metadata once and caches the resulting fixed gain.
/// No audio filter is used during playback, so the media timeline is unchanged.
/// </summary>
internal static class LoudnessCache
{
    private static string CacheFile => Path.Combine(DataLocation.Root, "loudness.json");

    // Shared in-memory copy: two overlapping loads (fast track switches) used
    // to each load their own dictionary and the later full-file save wiped the
    // earlier entry — those files were rescanned on every play. Keys compare
    // case-insensitively (same file via different-case paths shares the entry).
    private static readonly object Gate = new();
    private static Dictionary<string, double>? _cache;

    public static async Task<double> GetGainDbAsync(string audioPath)
    {
        // Everything (stat + file IO + ffmpeg) runs off the caller's thread —
        // the stat and cache-file read used to run on the UI thread per switch.
        return await Task.Run(() => GetGainDbCore(audioPath));
    }

    /// <summary>
    /// Warm the cache for a track that is likely to play next. A cold miss
    /// runs the ffmpeg loudness scan in the background (up to two minutes on
    /// an uncached file), so the eventual switch applies its gain instantly
    /// instead of blocking the load. Cheap no-op when already cached; safe to
    /// call repeatedly.
    /// </summary>
    public static void WarmAsync(string? audioPath)
    {
        if (string.IsNullOrEmpty(audioPath) || !System.IO.File.Exists(audioPath))
            return;
        _ = Task.Run(() =>
        {
            try
            {
                var key = new LoudnessKey(audioPath, System.IO.File.GetLastWriteTimeUtc(audioPath).Ticks).ToString();
                lock (Gate)
                {
                    _cache ??= LoadSync();
                    if (_cache.ContainsKey(key))
                        return; // already warm
                }
                GetGainDbCore(audioPath); // fills the cache (miss path)
            }
            catch
            {
                // Pre-warming is best effort; the real load path re-runs it.
            }
        });
    }

    private static double GetGainDbCore(string audioPath)
    {
        try
        {
            var key = new LoudnessKey(audioPath, System.IO.File.GetLastWriteTimeUtc(audioPath).Ticks).ToString();

            lock (Gate)
            {
                if (_cache == null)
                    _cache = LoadSync();
                if (_cache.TryGetValue(key, out var cached))
                    return cached;
            }

            var gain = ReadReplayGain(audioPath);
            if (!gain.HasValue)
                gain = AnalyzeWithFfmpegAsync(audioPath).GetAwaiter().GetResult();

            // Null here means the analysis FAILED (no ffmpeg.exe, non-zero
            // exit code, unparsable output, 2-minute timeout kill, exception)
            // — not "the track measures at 0 dB". Writing that failure to the
            // cache as 0 pinned it until the file's mtime changed, so one
            // transient problem (missing ffmpeg, sleeping drive) muted the
            // normalization for that file forever. Return the default gain for
            // THIS play and leave the cache untouched: the key carries the
            // mtime, so the next play retries the scan. A genuine 0 dB result
            // (input_i of -14 LUFS) still arrives as a non-null 0 and is
            // cached as before.
            if (!gain.HasValue)
                return 0;

            lock (Gate)
            {
                _cache[key] = gain.Value;
                SaveSync(_cache);
            }
            return gain.Value;
        }
        catch
        {
            return 0;
        }
    }

    private static double? ReadReplayGain(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var tag = file.Tag;
            var gain = tag.ReplayGainTrackGain;
            return double.IsFinite(gain) ? Math.Clamp(gain, -12, 6) : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<double?> AnalyzeWithFfmpegAsync(string path)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (!System.IO.File.Exists(executable))
            return null;

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-nostats");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(path);
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-sn");
        startInfo.ArgumentList.Add("-dn");
        startInfo.ArgumentList.Add("-af");
        startInfo.ArgumentList.Add("loudnorm=I=-14:TP=-1.0:LRA=11:print_format=json");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("null");
        startInfo.ArgumentList.Add("NUL");

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            try
            {
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                await Task.WhenAll(stderr, stdout);

                // Kill on timeout: the old version only abandoned the await,
                // leaving a stuck ffmpeg.exe alive forever.
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));

                if (process.ExitCode != 0)
                    return null;

                var match = Regex.Match(stderr.Result, "\"input_i\"\\s*:\\s*\"(?<lufs>-?[0-9]+(?:\\.[0-9]+)?)\"");
                if (!match.Success || !double.TryParse(
                        match.Groups["lufs"].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var inputLufs))
                    return null;

                return Math.Clamp(-14.0 - inputLufs, -12, 6);
            }
            catch (TimeoutException)
            {
                try { process.Kill(true); } catch { }
                return null;
            }
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, double> LoadSync()
    {
        try
        {
            if (!System.IO.File.Exists(CacheFile))
                return new Dictionary<string, double>(CaseInsensitiveComparer);
            using var stream = System.IO.File.OpenRead(CacheFile);
            var deserialized = JsonSerializer.Deserialize<Dictionary<string, double>>(stream);
            // The deserializer builds a case-SENSITIVE dictionary — without
            // this rebuild, an entry saved under a different-case path would
            // not be found after a restart and the file would be rescanned,
            // defeating the case-insensitive comparer used everywhere else.
            return deserialized == null
                ? new Dictionary<string, double>(CaseInsensitiveComparer)
                : new Dictionary<string, double>(deserialized, CaseInsensitiveComparer);
        }
        catch
        {
            return new Dictionary<string, double>(CaseInsensitiveComparer);
        }
    }

    private static readonly StringComparer CaseInsensitiveComparer = StringComparer.OrdinalIgnoreCase;

    private static void SaveSync(Dictionary<string, double> cache)
    {
        try
        {
            Directory.CreateDirectory(DataLocation.Root);
            var json = JsonSerializer.Serialize(cache);
            AtomicFile.WriteAllText(CacheFile, json, System.Text.Encoding.UTF8);
        }
        catch
        {
        }
    }

    private readonly record struct LoudnessKey(string Path, long Mtime)
    {
        public override string ToString() => $"{Path}|{Mtime}";
    }
}
