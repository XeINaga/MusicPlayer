using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MusicPlayer.Services;

/// <summary>A single QQ Music search result.</summary>
public sealed record QQSong(string Title, string Artist, string Album, string SongMid, int DurationSec);

/// <summary>
/// Lyrics search / download against QQ Music's public web endpoints
/// (verified against the live service):
///  - search: c.y.qq.com/soso/fcgi-bin/client_search_cp  (requires Referer y.qq.com)
///  - lyrics: c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg (requires
///    Referer music.qq.com; returns plain LRC via nobase64=1)
/// Translation ("trans") is returned when QQ provides it without login —
/// usually only empty for non-Chinese songs these days; romaji is not served
/// by these endpoints (kept null).
/// </summary>
public static class QQLyricService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient();
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        c.Timeout = TimeSpan.FromSeconds(10);
        return c;
    }

    private static async Task<string?> GetAsync(string url, string referer)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Referrer = new Uri(referer);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch
        {
            return null; // offline / blocked / timeout — callers treat as "no result"
        }
    }

    /// <summary>Search songs by keyword; returns candidates with songmid for lyrics.</summary>
    public static async Task<List<QQSong>> SearchAsync(string keyword, int limit = 20)
    {
        var results = new List<QQSong>();
        if (string.IsNullOrWhiteSpace(keyword))
            return results;

        var url = "https://c.y.qq.com/soso/fcgi-bin/client_search_cp" +
                  $"?w={Uri.EscapeDataString(keyword)}&format=json&cr=1&n={limit}";

        var json = await GetAsync(url, "https://y.qq.com/");
        if (json == null)
            return results;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("song", out var song) ||
            !song.TryGetProperty("list", out var list) ||
            list.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var s in list.EnumerateArray())
        {
            var mid = GetString(s, "songmid");
            if (string.IsNullOrEmpty(mid))
                continue;

            var title = GetString(s, "songname") ?? "";
            var artist = "";
            if (s.TryGetProperty("singer", out var singers) && singers.ValueKind == JsonValueKind.Array)
            {
                var names = singers.EnumerateArray()
                    .Select(x => GetString(x, "name"))
                    .Where(n => !string.IsNullOrEmpty(n));
                artist = string.Join("/", names);
            }
            var album = GetString(s, "albumname") ?? "";
            var duration = 0;
            if (s.TryGetProperty("interval", out var iv) && iv.ValueKind == JsonValueKind.Number)
                duration = iv.GetInt32();

            results.Add(new QQSong(title, artist, album, mid, duration));
        }

        return results;
    }

    /// <summary>
    /// Fetch lyrics (original / translation / romaji) for one song. Any of the
    /// three may be null/empty when QQ Music has no such version available
    /// without login.
    /// </summary>
    public static async Task<(string? Lyric, string? Trans, string? Roma)?> FetchLyricAsync(string songMid)
    {
        var url = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg" +
                  $"?songmid={Uri.EscapeDataString(songMid)}&g_tk=5381&format=json&nobase64=1" +
                  "&inCharset=utf8&outCharset=utf-8";

        var json = await GetAsync(url, "https://music.qq.com/");
        if (json == null)
            return null;

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("retcode", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.GetInt32() != 0)
            return null;

        var lyric = GetString(root, "lyric");
        var trans = GetString(root, "trans");
        return (NullIfEmpty(lyric), NullIfEmpty(trans), null);
    }

    // ---------- helpers ----------

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? GetString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
