using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>
/// Parses timed lyrics for an audio file.
///
/// Supported file formats (resolved next to the audio by base name):
///   - .lrc / .txt  : LRC-style timestamps  [mm:ss.xx]
///   - .srt         : SubRip subtitles      00:00:01,000 --> 00:00:04,000
///
/// Files are read with best-effort encoding detection (UTF-8 / UTF-8-BOM /
/// UTF-16 "Unicode" / UTF-32, with a GBK fallback) via <see cref="EncodingHelper"/>.
///
/// Multi-language layout (original / romaji / translation):
///   1. Main file: several text lines sharing one timestamp are assigned in
///      order to Original, Romaji, Translation.
///   2. Companion files:  "&lt;name&gt;.romaji.lrc" / ".translation.lrc" / ".zh.lrc" ...
/// </summary>
public static class LyricsParser
{
    // [mm:ss.xx]  or  [mm:ss.xxx]  (accepts '.' or ':' as fraction separator)
    private static readonly Regex TimeTag =
        new(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    // Enhanced-LRC word-level tags: <mm:ss.xx> ... stripped from the text.
    private static readonly Regex WordTimeTag =
        new(@"<\d{1,2}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    // [offset:±ms] metadata tag (positive = show lyrics earlier).
    private static readonly Regex OffsetTag =
        new(@"\[offset\s*:\s*([+-]?\d+)\s*\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] RomajiSuffixes = { ".romaji", ".roma", ".rom" };

    private static readonly string[] TranslationSuffixes =
        { ".tr", ".trans", ".translation", ".zh", ".chs", ".cn", ".en" };

    public static LyricDocument? Parse(string audioPath)
    {
        var explicitLyric = string.IsNullOrWhiteSpace(audioPath) ? null : LyricBindingStore.Get(audioPath);
        return Parse(audioPath, explicitLyric);
    }

    /// <summary>
    /// Parse lyrics for an audio file.
    /// When <paramref name="explicitLyricPath"/> is provided (a user-assigned file),
    /// it is used as the main lyrics source, overriding auto-detection. Companion
    /// files (romaji / translation) are still merged by the audio's base name.
    /// <paramref name="forcedEncoding"/> ("gbk"/"shift_jis"/"big5"/"utf-8"/null=auto)
    /// overrides charset detection for every lyric file read.
    /// </summary>
    public static LyricDocument? Parse(string audioPath, string? explicitLyricPath, string? forcedEncoding = null)
    {
        if (string.IsNullOrWhiteSpace(audioPath))
            return null;

        var dir = Path.GetDirectoryName(audioPath) ?? string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(audioPath);
        var basePath = Path.Combine(dir, baseName);

        var doc = new LyricDocument();
        var map = new Dictionary<System.TimeSpan, LyricLine>();

        // 1) Main lyrics file: explicit assignment wins, otherwise auto-detect.
        var mainFile = (explicitLyricPath != null && File.Exists(explicitLyricPath))
            ? explicitLyricPath
            : FindMainLyric(basePath, forcedEncoding);

        if (mainFile != null)
        {
            if (mainFile.EndsWith(".srt", System.StringComparison.OrdinalIgnoreCase))
                MergeSrt(mainFile, doc, map, forcedEncoding);
            else
                MergeMain(mainFile, doc, map, forcedEncoding);
        }

        // 2) Companion files (LRC only; timestamps merge by time)
        foreach (var s in RomajiSuffixes)
        {
            var f = basePath + s + ".lrc";
            if (File.Exists(f))
                MergeCompanion(f, doc, map, LyricRole.Romaji, forcedEncoding);
        }

        foreach (var s in TranslationSuffixes)
        {
            var f = basePath + s + ".lrc";
            if (File.Exists(f))
                MergeCompanion(f, doc, map, LyricRole.Translation, forcedEncoding);
        }

        if (doc.Lines.Count == 0)
            return null;

        doc.Lines.Sort((a, b) => a.Time.CompareTo(b.Time));
        return doc;
    }

    private static string? FindMainLyric(string basePath, string? enc)
    {
        var lrc = basePath + ".lrc";
        if (File.Exists(lrc))
            return lrc;

        var srt = basePath + ".srt";
        if (File.Exists(srt))
            return srt;

        var txt = basePath + ".txt";
        if (File.Exists(txt) && EncodingHelper.ReadText(txt, enc).Contains('['))
            return txt;

        return null;
    }

    // ---------- LRC / TXT ----------

    private static void MergeMain(string path, LyricDocument doc, Dictionary<System.TimeSpan, LyricLine> map, string? enc)
    {
        var text = EncodingHelper.ReadText(path, enc);

        // Honor the file's own [offset:±ms] tag (positive = earlier).
        var offsetMs = 0;
        var om = OffsetTag.Match(text);
        if (om.Success && int.TryParse(om.Groups[1].Value, out var parsedOffset))
            offsetMs = parsedOffset;

        var raw = ParseLrc(text, offsetMs);
        var grouped = raw.GroupBy(r => r.Time).OrderBy(g => g.Key);
        foreach (var g in grouped)
        {
            var line = GetOrCreate(map, doc, g.Key);
            var texts = g.Select(x => x.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (texts.Count > 0) line.Original = texts[0];
            if (texts.Count > 1) line.Romaji = texts[1];
            if (texts.Count > 2) line.Translation = texts[2];
        }
    }

    private static void MergeCompanion(string path, LyricDocument doc, Dictionary<System.TimeSpan, LyricLine> map, LyricRole role, string? enc)
    {
        var raw = ParseLrc(EncodingHelper.ReadText(path, enc));
        var grouped = raw.GroupBy(r => r.Time).OrderBy(g => g.Key);
        foreach (var g in grouped)
        {
            var line = GetOrCreate(map, doc, g.Key);
            var text = g.Select(x => x.Text).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (text == null)
                continue;

            switch (role)
            {
                case LyricRole.Romaji:
                    line.Romaji = text;
                    break;
                case LyricRole.Translation:
                    line.Translation = text;
                    break;
            }
        }
    }

    private static List<(System.TimeSpan Time, string Text)> ParseLrc(string text, int offsetMs = 0)
    {
        var result = new List<(System.TimeSpan, string)>();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = TimeTag.Matches(line);
            if (matches.Count == 0)
                continue;

            // Strip enhanced-LRC word tags so "<00:01.50>word" becomes "word".
            var body = WordTimeTag.Replace(TimeTag.Replace(line, string.Empty), string.Empty).Trim();

            foreach (Match m in matches)
            {
                var minutes = int.Parse(m.Groups[1].Value);
                var seconds = int.Parse(m.Groups[2].Value);
                var fracStr = m.Groups[3].Value;

                int milliseconds = 0;
                if (fracStr.Length > 0)
                {
                    var frac = int.Parse(fracStr);
                    milliseconds = fracStr.Length switch
                    {
                        1 => frac * 100,
                        2 => frac * 10,
                        _ => frac
                    };
                }

                var time = new System.TimeSpan(0, 0, minutes, seconds, milliseconds);
                if (offsetMs != 0)
                    time -= System.TimeSpan.FromMilliseconds(offsetMs);
                result.Add((time, body));
            }
        }

        return result;
    }

    // ---------- SRT ----------

    private static void MergeSrt(string path, LyricDocument doc, Dictionary<System.TimeSpan, LyricLine> map, string? enc)
    {
        var text = EncodingHelper.ReadText(path, enc)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n');

        var lines = text.Split('\n');
        var i = 0;

        while (i < lines.Length)
        {
            // Skip leading blank lines.
            while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i]))
                i++;
            if (i >= lines.Length)
                break;

            // An SRT cue may start with a numeric index line.
            var timeLine = i;
            if (int.TryParse(lines[i].Trim(), out _) && i + 1 < lines.Length && lines[i + 1].Contains("-->"))
                timeLine = i + 1;

            var arrow = lines[timeLine].IndexOf("-->", System.StringComparison.Ordinal);
            if (arrow < 0)
            {
                i = timeLine + 1;
                continue;
            }

            var start = ParseSrtTime(lines[timeLine].Substring(0, arrow).Trim());
            if (start == null)
            {
                i = timeLine + 1;
                continue;
            }

            // Collect text lines until a blank line or the next cue.
            var texts = new List<string>();
            var j = timeLine + 1;
            while (j < lines.Length && !string.IsNullOrWhiteSpace(lines[j]))
            {
                var l = lines[j];
                var nextArrow = l.IndexOf("-->", System.StringComparison.Ordinal);
                if (nextArrow >= 0)
                    break;
                if (int.TryParse(l.Trim(), out _) && j + 1 < lines.Length && lines[j + 1].Contains("-->"))
                    break;

                texts.Add(l.Trim());
                j++;
            }

            i = j;

            if (texts.Count == 0)
                continue;

            var line = GetOrCreate(map, doc, start.Value);
            if (string.IsNullOrWhiteSpace(line.Original)) line.Original = texts[0];
            if (texts.Count > 1 && string.IsNullOrWhiteSpace(line.Romaji)) line.Romaji = texts[1];
            if (texts.Count > 2 && string.IsNullOrWhiteSpace(line.Translation)) line.Translation = texts[2];
        }
    }

    private static System.TimeSpan? ParseSrtTime(string s)
    {
        var parts = s.Split(':');
        if (parts.Length == 3)
        {
            if (int.TryParse(parts[0], out var h) && int.TryParse(parts[1], out var m) &&
                double.TryParse(parts[2].Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var sec))
            {
                return System.TimeSpan.FromSeconds(h * 3600 + m * 60 + sec);
            }
        }
        else if (parts.Length == 2)
        {
            if (int.TryParse(parts[0], out var m) &&
                double.TryParse(parts[1].Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture, out var sec))
            {
                return System.TimeSpan.FromSeconds(m * 60 + sec);
            }
        }

        return null;
    }

    // ---------- Shared ----------

    private static LyricLine GetOrCreate(Dictionary<System.TimeSpan, LyricLine> map, LyricDocument doc, System.TimeSpan time)
    {
        if (map.TryGetValue(time, out var existing))
            return existing;

        var line = new LyricLine { Time = time };
        map[time] = line;
        doc.Lines.Add(line);
        return line;
    }

    private enum LyricRole
    {
        Original,
        Romaji,
        Translation
    }
}
