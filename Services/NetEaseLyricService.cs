using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MusicPlayer.Services;

/// <summary>
/// NetEase Cloud Music lyrics (music.163.com public web API — no login).
/// Unlike QQ Music's web endpoints, this one still serves TRANSLATION and
/// ROMAJI for Japanese songs, so it is the primary source for full three-line
/// lyrics; QQ Music is the fallback for the original lyric only.
/// Returns the shared QQSong shape (SongMid carries the numeric song id).
/// </summary>
public static class NetEaseLyricService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        c.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com/");
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

    /// <summary>Search songs; duration is in seconds (converted from NetEase ms).</summary>
    public static async Task<List<QQSong>> SearchAsync(string keyword, int limit = 20)
    {
        var results = new List<QQSong>();
        if (string.IsNullOrWhiteSpace(keyword))
            return results;

        var url = "https://music.163.com/api/search/get/web" +
                  $"?s={Uri.EscapeDataString(keyword)}&type=1&offset=0&limit={limit}";

        var json = await GetAsync(url);
        if (json == null)
            return results;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("songs", out var songs) ||
            songs.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var s in songs.EnumerateArray())
        {
            var id = s.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64().ToString()
                : null;
            if (string.IsNullOrEmpty(id))
                continue;

            var title = GetString(s, "name") ?? "";
            var artist = "";
            if (s.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array)
            {
                var names = artists.EnumerateArray()
                    .Select(x => GetString(x, "name"))
                    .Where(n => !string.IsNullOrEmpty(n));
                artist = string.Join("/", names);
            }
            var durationSec = 0;
            if (s.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                durationSec = (int)(dur.GetInt32() / 1000);

            results.Add(new QQSong(title, artist, "", id!, durationSec));
        }

        return results;
    }

    /// <summary>Fetch original / translation / romaji lyrics for one song id.</summary>
    public static async Task<(string? Lyric, string? Trans, string? Roma)?> FetchLyricAsync(string songId)
    {
        // lv=-1 original, tv=-1 translation, rv=-1 romaji (os=pc returns all).
        var url = $"https://music.163.com/api/song/lyric?os=pc&id={Uri.EscapeDataString(songId)}" +
                  "&lv=-1&kv=-1&tv=-1&rv=-1";

        var json = await GetAsync(url);
        if (json == null)
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? Pick(string section)
        {
            if (!root.TryGetProperty(section, out var el))
                return null;
            if (!el.TryGetProperty("lyric", out var lyricEl) || lyricEl.ValueKind != JsonValueKind.String)
                return null;
            var v = lyricEl.GetString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        return (Pick("lrc"), Pick("tlyric"), Pick("romalrc"));
    }

    private static string? GetString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
