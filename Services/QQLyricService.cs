using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MusicPlayer.Services;

/// <summary>A single QQ Music search result.</summary>
/// <param name="SongMid">QQ's string id, used by the plain LRC endpoint.</param>
/// <param name="SongId">
/// QQ's numeric id, required by the encrypted QRC endpoint. Sending the string
/// songmid there makes the service reply with musicid="0" and return no lyrics.
/// </param>
public sealed record QQSong(
    string Title, string Artist, string Album, string SongMid, int DurationSec,
    string SongId = "");

/// <summary>
/// Lyrics search / download against QQ Music's public web endpoints
/// (verified against the live service):
///  - search: c.y.qq.com/soso/fcgi-bin/client_search_cp  (requires Referer y.qq.com)
///  - plain LRC: c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg (requires
///    Referer music.qq.com; returns plain LRC via nobase64=1; includes a
///    Chinese translation "trans" when available)
///  - QRC (romaji + translation): c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg
///    returns hex-encoded, 3DES+zlib encrypted blobs decoded by QqQrcDecrypter.
/// NetEase is the primary romaji source; QQ provides romaji only via QRC.
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

            // songid is a JSON number; the QRC endpoint needs this numeric id.
            var songId = GetNumber(s, "songid") ?? "";

            results.Add(new QQSong(title, artist, album, mid, duration, songId));
        }

        return results;
    }

    /// <summary>
    /// Fetch lyrics (original / translation / romaji) for one song. Any of the
    /// three may be null/empty when QQ Music has no such version available.
    /// Original + translation come from the plain LRC endpoint (reliable);
    /// romaji (and an alternate translation) come from the encrypted QRC endpoint.
    /// </summary>
    /// <param name="songMid">QQ string id (plain LRC endpoint).</param>
    /// <param name="songId">
    /// QQ numeric id for the QRC endpoint. When omitted, the QRC lookup is
    /// skipped because the service cannot resolve a string songmid.
    /// </param>
    public static async Task<(string? Lyric, string? Trans, string? Roma)?> FetchLyricAsync(
        string songMid, string? songId = null)
    {
        string? lyric = null, trans = null, roma = null;

        // 1) Plain LRC endpoint — reliable original lyric + Chinese translation.
        var plainUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg" +
                       $"?songmid={Uri.EscapeDataString(songMid)}&g_tk=5381&format=json&nobase64=1" +
                       "&inCharset=utf8&outCharset=utf-8";

        var json = await GetAsync(plainUrl, "https://music.qq.com/");
        if (json != null)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!(root.TryGetProperty("retcode", out var rc) && rc.ValueKind == JsonValueKind.Number && rc.GetInt32() != 0))
            {
                lyric = NullIfEmpty(GetString(root, "lyric"));
                trans = NullIfEmpty(GetString(root, "trans"));
            }
        }

        // 2) QRC endpoint — encrypted; provides romaji (+ translation where available).
        //    This endpoint only accepts the NUMERIC songid.
        if (!string.IsNullOrWhiteSpace(songId))
        {
            try
            {
                var qrcUrl = "https://c.y.qq.com/qqmusic/fcgi-bin/lyric_download.fcg" +
                             $"?version=15&miniversion=82&lrctype=4&musicid={Uri.EscapeDataString(songId)}";
                var qrc = await GetAsync(qrcUrl, "https://c.y.qq.com/");
                if (qrc != null)
                {
                    var (qLyric, qTrans, qRoma) = QqQrcDecrypter.ParseQrc(qrc);

                    // First source wins for every field. The plain endpoint runs
                    // first and is the more reliable of the two, so QRC only ever
                    // fills gaps rather than overwriting a good translation.
                    if (qLyric != null) lyric ??= qLyric;
                    if (qTrans != null) trans ??= qTrans;
                    if (qRoma != null) roma ??= qRoma;
                }
            }
            catch
            {
                // offline / blocked / unsupported — fall back to plain LRC above
            }
        }

        if (lyric == null && trans == null && roma == null)
            return null;
        return (lyric, trans, roma);
    }

    // ---------- helpers ----------

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? GetString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>Read a JSON number as its raw text (songid is numeric).</summary>
    private static string? GetNumber(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetRawText()
            : null;
}
