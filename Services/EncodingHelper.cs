using System;
using System.IO;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>
/// Detects text file encodings (UTF-8, UTF-8-BOM, UTF-16/Unicode, UTF-32)
/// by inspecting the byte-order mark, falling back to UTF-8 (strict) and
/// finally to the system default (GBK on Chinese Windows) for files without a BOM.
/// </summary>
public static class EncodingHelper
{
    /// <summary>
    /// Read a text file with best-effort encoding detection. Pass a forced
    /// encoding name ("gbk" / "shift_jis" / "big5" / "utf-8") to override
    /// detection — useful for e.g. Shift-JIS lyric files that GBK mis-decodes.
    /// </summary>
    public static string ReadText(string path, string? forcedEncodingName = null)
    {
        if (!File.Exists(path))
            return string.Empty;

        var bytes = File.ReadAllBytes(path);
        var forced = ResolveForced(forcedEncodingName);
        return (forced ?? Detect(bytes)).GetString(bytes);
    }

    private static Encoding? ResolveForced(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return name.ToLowerInvariant() switch
            {
                "utf-8" => new UTF8Encoding(false),
                _ => Encoding.GetEncoding(name),
            };
        }
        catch
        {
            return null; // unknown name / codepage provider missing -> auto
        }
    }

    /// <summary>Detect the most likely encoding for the given bytes.</summary>
    public static Encoding Detect(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return new UTF8Encoding(false); // UTF-8 with BOM

        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                return new UTF32Encoding(false, true); // UTF-32 LE (BOM)
            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                return new UTF32Encoding(true, true);  // UTF-32 BE (BOM)
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                return new UnicodeEncoding(false, true); // UTF-16 LE (BOM) = "Unicode"
            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                return new UnicodeEncoding(true, true);  // UTF-16 BE (BOM)
        }

        // No BOM: try strict UTF-8 first, then legacy CJK codepages.
        // QQ Music LRC files are typically GBK (CP936); some Japanese tools emit
        // Shift-JIS. GBK is tried first because it is the dominant case and also
        // decodes the Japanese text in those files correctly.
        if (IsValidUtf8(bytes))
            return new UTF8Encoding(false);

        foreach (var name in new[] { "GBK", "shift_jis", "Big5" })
        {
            try
            {
                var enc = Encoding.GetEncoding(name);
                // GBK/Shift-JIS cover every byte pair, so instead of relying on
                // replacement chars, just prefer GBK (first) for this app's files.
                return enc;
            }
            catch
            {
                // try next codepage
            }
        }

        return Encoding.Default;
    }

    private static bool IsValidUtf8(byte[] b)
    {
        var i = 0;
        while (i < b.Length)
        {
            var c = b[i];
            if (c < 0x80)
            {
                i++;
                continue;
            }

            int extra;
            if ((c & 0xE0) == 0xC0) extra = 1;
            else if ((c & 0xF0) == 0xE0) extra = 2;
            else if ((c & 0xF8) == 0xF0) extra = 3;
            else return false;

            if (i + extra >= b.Length)
                return false;

            for (var j = 1; j <= extra; j++)
            {
                if ((b[i + j] & 0xC0) != 0x80)
                    return false;
            }

            i += extra + 1;
        }

        return true;
    }
}
