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

    /// <summary>
    /// Background warm-up for the WHOLE library (startup path). Random mode
    /// cannot predict the next track, and PreprocessNextTrack only covers
    /// list order — without this, every first play of an uncached track
    /// blocks the switch on a full ffmpeg scan (1-3 s per FLAC).
    /// Two concurrent scans: HDD-friendly, still finishes a 700-track
    /// library in a few minutes. Already-cached tracks cost one stat each.
    /// </summary>
    public static void WarmLibraryAsync(IEnumerable<string?> paths)
    {
        _ = Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(2);
            var tasks = new List<Task>();
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p) || !System.IO.File.Exists(p))
                    continue;
                var path = p;
                string key;
                try
                {
                    key = new LoudnessKey(path, System.IO.File.GetLastWriteTimeUtc(path).Ticks).ToString();
                }
                catch
                {
                    continue;
                }
                lock (Gate)
                {
                    _cache ??= LoadSync();
                    if (_cache.ContainsKey(key))
                        continue; // already warm
                }
                await gate.WaitAsync();
                tasks.Add(Task.Run(() =>
                {
                    try { GetGainDbCore(path); }
                    catch { /* best effort */ }
                    finally { gate.Release(); }
                }));
            }
            await Task.WhenAll(tasks.ToArray());
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

                // ffmpeg reports "-inf" for silent/near-silent input; the old
                // numeric-only pattern missed it, the miss was treated as
                // "analysis failed" (never cached), and every single play of a
                // silent track re-ran the full two-minute ffmpeg scan.
                var match = Regex.Match(stderr.Result, "\"input_i\"\\s*:\\s*\"(?<lufs>-?(?:[0-9]+(?:\\.[0-9]+)?|inf))\"");
                if (!match.Success)
                    return null;

                double inputLufs;
                var lufsText = match.Groups["lufs"].Value;
                if (lufsText.EndsWith("inf", StringComparison.Ordinal))
                {
                    // -inf clamps to the +6 dB ceiling below; the samples are
                    // all zero, so the gain is inaudible either way — the
                    // point is that the value gets CACHED and the scan never
                    // re-runs. "+inf" (not produced in practice) clamps to -12.
                    inputLufs = lufsText.StartsWith('-') ? double.NegativeInfinity : double.PositiveInfinity;
                }
                else if (!double.TryParse(
                        lufsText,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out inputLufs))
                {
                    return null;
                }

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

    // v2 on-disk format: {"V":2,"Gains":{...}}. v1 (a plain dictionary) is
    // migrated on load — see LoadSync for the 0 dB purge that migration does.
    private sealed record LoudnessFile(int V, Dictionary<string, double> Gains);

    private static Dictionary<string, double> LoadSync()
    {
        var result = new Dictionary<string, double>(CaseInsensitiveComparer);
        try
        {
            if (!System.IO.File.Exists(CacheFile))
                return result;

            Dictionary<string, double>? raw = null;
            try
            {
                using (var stream = System.IO.File.OpenRead(CacheFile))
                {
                    var v2 = JsonSerializer.Deserialize<LoudnessFile>(stream);
                    if (v2?.Gains != null)
                        raw = v2.Gains;
                }
            }
            catch (JsonException)
            {
                // not the v2 shape — fall through to the v1 format below
            }

            if (raw == null)
            {
                try
                {
                    using var stream = System.IO.File.OpenRead(CacheFile);
                    raw = JsonSerializer.Deserialize<Dictionary<string, double>>(stream);
                }
                catch (JsonException)
                {
                    // corrupt file — start empty, it rebuilds from scans
                }
            }

            if (raw == null)
                return result;

            var purged = false;
            foreach (var (key, value) in raw)
            {
                // v1-era poison: the old "cache a failed scan as 0" bug pinned
                // 0 dB gains on tracks that actually measure 2-9 dB quieter,
                // so they played noticeably louder than everything else. Real
                // 0 dB entries (a track measuring exactly -14 LUFS) are rare
                // enough that dropping them too is fine — one rescan each.
                if (value == 0 || string.IsNullOrEmpty(key))
                {
                    purged = true;
                    continue;
                }
                result[key] = value;
            }

            if (purged)
            {
                // Persist the migration immediately (v2 shape) so the purge
                // runs once instead of on every launch.
                try { SaveSync(result); } catch { /* best effort */ }
            }
            return result;
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
            var json = JsonSerializer.Serialize(new LoudnessFile(2, cache));
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
