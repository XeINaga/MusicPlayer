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

    public static async Task<double> GetGainDbAsync(string audioPath)
    {
        try
        {
            var key = new LoudnessKey(audioPath, System.IO.File.GetLastWriteTimeUtc(audioPath).Ticks);
            var cache = await LoadAsync();
            if (cache.TryGetValue(key.ToString(), out var cached))
                return cached;

            var gain = await Task.Run(() => ReadReplayGain(audioPath));
            if (!gain.HasValue)
                gain = await AnalyzeWithFfmpegAsync(audioPath);
            cache[key.ToString()] = gain ?? 0;
            await SaveAsync(cache);
            return gain ?? 0;
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
            var stderr = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEndAsync();
            await Task.WhenAll(stderr, stdout);
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
        catch
        {
            return null;
        }
    }

    private static async Task<System.Collections.Generic.Dictionary<string, double>> LoadAsync()
    {
        try
        {
            if (!System.IO.File.Exists(CacheFile))
                return new();
            await using var stream = System.IO.File.OpenRead(CacheFile);
            return await JsonSerializer.DeserializeAsync<System.Collections.Generic.Dictionary<string, double>>(stream)
                   ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static async Task SaveAsync(System.Collections.Generic.Dictionary<string, double> cache)
    {
        try
        {
            Directory.CreateDirectory(DataLocation.Root);
            var json = JsonSerializer.Serialize(cache);
            await Task.Run(() => AtomicFile.WriteAllText(CacheFile, json, System.Text.Encoding.UTF8));
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
