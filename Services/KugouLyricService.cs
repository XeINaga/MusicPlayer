using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace MusicPlayer.Services;

/// <summary>
/// KuGou Music lyrics — search + plain LRC + word-timed KRC, no login.
///
/// Endpoints (mobile web API):
///   search : http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword=...
///            → data.info[] { songname, singername, album_name, duration(sec), hash }
///   match  : http://krcs.kugou.com/search?ver=1&man=yes&client=mobi&hash=...
///            → candidates[0] { id, accesskey }
///   download: http://lyrics.kugou.com/download?ver=1&client=pc&id=..&accesskey=..&fmt=krc|lrc&charset=utf8
///            → { content: base64, fmt } — KRC content is XOR-encrypted zlib
///              (see <see cref="KrcDecrypter"/>), LRC content is plain base64.
///
/// Returns the shared QQSong shape; SongMid carries the song FILE hash the
/// lyric matcher keys on.
/// </summary>
public static class KugouLyricService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        c.Timeout = TimeSpan.FromSeconds(20);
        return c;
    }

    private static async Task<string?> GetAsync(string url, int attempts = 3)
    {
        // The KuGou endpoints are plain http and occasionally slow / flaky;
        // a single 10s timeout used to surface as "no lyrics available".
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                using var resp = await Http.GetAsync(url);
                if (resp.IsSuccessStatusCode)
                    return await resp.Content.ReadAsStringAsync();
                if ((int)resp.StatusCode is 404 or 502 or 503 && attempt < attempts)
                {
                    await Task.Delay(600 * attempt);
                    continue;
                }
                return null;
            }
            catch when (attempt < attempts)
            {
                await Task.Delay(600 * attempt);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    /// <summary>Search songs; duration is in seconds.</summary>
    public static async Task<List<QQSong>> SearchAsync(string keyword, int limit = 20)
    {
        var results = new List<QQSong>();
        if (string.IsNullOrWhiteSpace(keyword))
            return results;

        var url = "http://mobilecdn.kugou.com/api/v3/search/song?format=json" +
                  $"&keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize={limit}";

        var json = await GetAsync(url);
        if (json == null)
            return results;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var s in info.EnumerateArray())
            {
                var hash = GetString(s, "hash");
                if (string.IsNullOrEmpty(hash))
                    continue;

                var title = GetString(s, "songname") ?? "";
                var artist = "";
                if (s.TryGetProperty("singername", out var ar) && ar.ValueKind == JsonValueKind.String)
                    artist = ar.GetString() ?? "";
                var album = "";
                if (s.TryGetProperty("album_name", out var al) && al.ValueKind == JsonValueKind.String)
                    album = al.GetString() ?? "";
                var durationSec = 0;
                if (s.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
                    durationSec = dur.GetInt32();

                results.Add(new QQSong(title, artist, album, hash!, durationSec));
            }
        }
        catch (JsonException)
        {
            // anti-bot HTML / malformed body — empty result, caller handles it
        }

        return results;
    }

    /// <summary>
    /// Fetch lyrics for a song hash. Returns (plainLrc, wordTimedKrc) — either
    /// may be null when KuGou has no such version for the track.
    /// </summary>
    public static async Task<(string? Lrc, string? Krc)> FetchLyricAsync(string hash)
    {
        // Match a lyric candidate for this audio hash.
        var matchUrl = "http://krcs.kugou.com/search?ver=1&man=yes&client=mobi&keyword=&duration=&hash=" +
                       Uri.EscapeDataString(hash);
        var matchJson = await GetAsync(matchUrl);
        if (matchJson == null)
            throw new Exception("酷狗歌词匹配请求失败（网络超时，请重试）");
        string? id = null, accessKey = null;
        try
        {
            var matchDoc = JsonDocument.Parse(matchJson);
            if (matchDoc.RootElement.TryGetProperty("candidates", out var cands) &&
                cands.ValueKind == JsonValueKind.Array &&
                cands.GetArrayLength() > 0)
            {
                var c0 = cands[0];
                id = GetString(c0, "id");
                accessKey = GetString(c0, "accesskey");
            }
        }
        catch (JsonException)
        {
            throw new Exception("酷狗歌词匹配返回了无法解析的数据（可能被反爬拦截）");
        }

        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(accessKey))
            throw new Exception("酷狗没有这首歌的歌词候选（曲目可能未收录）");

        var lrcTask = DownloadAsync(id!, accessKey!, "lrc");
        var krcTask = DownloadAsync(id!, accessKey!, "krc");
        await Task.WhenAll(lrcTask, krcTask);
        return (lrcTask.Result, krcTask.Result);
    }

    private static async Task<string?> DownloadAsync(string id, string accessKey, string fmt)
    {
        var url = "http://lyrics.kugou.com/download?ver=1&client=pc&charset=utf8" +
                  $"&id={Uri.EscapeDataString(id)}&accesskey={Uri.EscapeDataString(accessKey)}&fmt={fmt}";

        var json = await GetAsync(url);
        if (json == null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("content", out var contentEl) ||
                contentEl.ValueKind != JsonValueKind.String)
                return null;

            var bytes = Convert.FromBase64String(contentEl.GetString()!);
            if (fmt == "krc")
                return KrcDecrypter.Decrypt(bytes);

            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetString(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
