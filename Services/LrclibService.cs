using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MusicPlayer.Services;

/// <summary>
/// LRCLIB lyrics (lrclib.net public API — no login).
/// An open-source lyrics database that serves synced (time-tagged) LRC.
/// Used as a third fallback after NetEase and QQ Music.
/// Returns the shared QQSong shape (SongMid carries the LRCLIB track id).
/// </summary>
public static class LrclibService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "MusicPlayer/1.0 (https://github.com/example/MusicPlayer)");
        c.Timeout = TimeSpan.FromSeconds(10);
        return c;
    }

    private static async Task<string?> GetAsync(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Search songs by keyword; returns candidates with LRCLIB track id.</summary>
    public static async Task<List<QQSong>> SearchAsync(string keyword, int limit = 20)
    {
        var results = new List<QQSong>();
        if (string.IsNullOrWhiteSpace(keyword))
            return results;

        var url = "https://lrclib.net/api/search" +
                  $"?q={Uri.EscapeDataString(keyword)}";

        var json = await GetAsync(url);
        if (json == null)
            return results;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
            return results;

        var count = 0;
        foreach (var s in root.EnumerateArray())
        {
            if (count >= limit)
                break;

            var id = s.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64().ToString()
                : null;
            if (string.IsNullOrEmpty(id))
                continue;

            var title = GetString(s, "trackName") ?? "";
            var artist = GetString(s, "artistName") ?? "";
            var album = GetString(s, "albumName") ?? "";
            var duration = 0;
            if (s.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
            {
                // LRCLIB "duration" is SECONDS and frequently fractional
                // (e.g. 230.736) — GetInt64() on it throws FormatException
                // ("One of the identified items was in an invalid format.").
                duration = (int)Math.Round(dur.GetDouble());
            }

            results.Add(new QQSong(title, artist, album, id!, duration));
            count++;
        }

        return results;
    }

    /// <summary>
    /// Fetch synced lyrics (original LRC with time tags) for one LRCLIB track id.
    /// LRCLIB does not serve translation or romaji, so those are always null.
    /// </summary>
    public static async Task<(string? Lyric, string? Trans, string? Roma)?> FetchLyricAsync(string trackId)
    {
        var url = $"https://lrclib.net/api/get?id={Uri.EscapeDataString(trackId)}";

        var json = await GetAsync(url);
        if (json == null)
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // LRCLIB returns syncedLyrics as a string with LRC time tags.
        var lyric = GetString(root, "syncedLyrics");
        if (string.IsNullOrWhiteSpace(lyric))
            return null;

        return (lyric.Trim(), null, null);
    }

    // ---------- helpers ----------

    private static string? GetString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
