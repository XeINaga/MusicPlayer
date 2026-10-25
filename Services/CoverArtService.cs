using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace MusicPlayer.Services;

/// <summary>One album-art search hit.</summary>
/// <param name="Album">Album title as the source named it.</param>
/// <param name="Artist">Album artist (may be empty).</param>
/// <param name="PicUrl">Full-resolution image URL (embed this one).</param>
/// <param name="ThumbUrl">Small image URL for list rows.</param>
/// <param name="Source">"网易云" / "iTunes" — shown in the result row.</param>
/// <param name="Extra">Trailing info, e.g. track count / year.</param>
public sealed record CoverSearchResult(
    string Album, string Artist, string PicUrl, string ThumbUrl, string Source, string Extra);

/// <summary>
/// Album-art search + embedding for the cover-completion feature.
/// Sources (both login-free, verified reachable from CN networks):
///  - NetEase album search: music.163.com/api/search/get/web?type=10 — picUrl
///    scales via "?param=WxH" (the classic NetEase image-server trick).
///  - QQ Music album search: musicu.fcg search_type=2 → body.album.list[];
///    art URL built from the albumMID (T002R800x800M000{mid}.jpg, 800x800).
///    Needs the y.qq.com cookie (reused from QQLyricService).
///  - KuGou: song search resolves the album id, then the album-info endpoint
///    returns an imgurl TEMPLATE ("stdmusic/{size}/...") — {size}→480.
///  - iTunes Search API: itunes.apple.com/search?entity=album — JP and CN
///    storefronts merged (CJK catalogues live mostly on the JP store);
///    artworkUrl100 upscales by swapping the size segment.
/// Embedding uses TagLibSharp — the image goes INTO the audio file as the
/// front-cover picture, which is what CoverCache / MetadataService read.
/// </summary>
public static class CoverArtService
{
    public const int SourceAuto = 0, SourceNetEase = 1, SourceItunes = 2, SourceQq = 3, SourceKugou = 4;

    /// <summary>Canonical source keys, in the default auto order. The settings
    /// page stores a permutation of these names.</summary>
    public static readonly string[] SourceNames = { "NetEase", "QQ", "Kugou", "Itunes" };

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    // ---------- Search ----------

    public static string SourceKeyToName(int source) => source switch
    {
        SourceNetEase => "NetEase",
        SourceQq => "QQ",
        SourceKugou => "Kugou",
        SourceItunes => "Itunes",
        _ => "NetEase",
    };

    public static int SourceNameToKey(string name) => name switch
    {
        "QQ" => SourceQq,
        "Kugou" => SourceKugou,
        "Itunes" => SourceItunes,
        _ => SourceNetEase,
    };

    /// <summary>Parse a stored "NetEase,QQ,Kugou,Itunes" permutation into
    /// source keys. Unknown/duplicate names are dropped; missing ones are
    /// appended in default order so the result is always a complete
    /// permutation (an empty/missing setting yields the default order).</summary>
    public static List<int> ParseOrder(string? order)
    {
        var result = new List<int>();
        if (!string.IsNullOrWhiteSpace(order))
        {
            foreach (var name in order.Split(','))
            {
                var key = SourceNameToKey(name.Trim());
                if (!result.Contains(key))
                    result.Add(key);
            }
        }
        foreach (var name in SourceNames)
        {
            var key = SourceNameToKey(name);
            if (!result.Contains(key))
                result.Add(key);
        }
        return result;
    }

    /// <summary>Search albums. <paramref name="source"/>: 0=auto, 1=NetEase,
    /// 2=iTunes, 3=QQ, 4=KuGou. Auto walks <paramref name="order"/> (the
    /// user-configured priority; defaults to NetEase → QQ → KuGou → iTunes)
    /// and returns the first source that produced hits.</summary>
    public static async Task<List<CoverSearchResult>> SearchAsync(
        string keyword, int source, int limit = 20, IReadOnlyList<int>? order = null)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return new List<CoverSearchResult>();

        if (source == SourceNetEase)
            return await SearchNetEaseAsync(keyword, limit);
        if (source == SourceItunes)
            return await SearchItunesAsync(keyword, limit);
        if (source == SourceQq)
            return await SearchQqAsync(keyword, limit);
        if (source == SourceKugou)
            return await SearchKugouAsync(keyword, limit);

        foreach (var s in order ?? ParseOrder(null))
        {
            var found = s switch
            {
                SourceQq => await SearchQqAsync(keyword, limit),
                SourceKugou => await SearchKugouAsync(keyword, limit),
                SourceItunes => await SearchItunesAsync(keyword, limit),
                _ => await SearchNetEaseAsync(keyword, limit),
            };
            if (found.Count > 0)
                return found;
        }
        return new List<CoverSearchResult>();
    }

    // ---------- QQ Music ----------

    /// <summary>Album search via the same musicu.fcg endpoint the lyric
    /// service uses — search_type=2 (albums). Logged-out searches return an
    /// empty list (code 2001), same as the lyric endpoints.</summary>
    private static async Task<List<CoverSearchResult>> SearchQqAsync(string keyword, int limit)
    {
        var results = new List<CoverSearchResult>();
        try
        {
            var body = JsonSerializer.Serialize(new
            {
                comm = new { ct = 19, cv = 1873, uin = "0", format = "json", platform = "yqq" },
                req = new
                {
                    method = "DoSearchForQQMusicDesktop",
                    module = "music.search.SearchCgiService",
                    param = new { search_type = 2, query = keyword, page_num = 1, num_per_page = limit }
                }
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            req.Headers.Referrer = new Uri("https://y.qq.com/n/ryqq/search");
            var cookie = QQLyricService.CurrentCookie;
            if (!string.IsNullOrEmpty(cookie))
                req.Headers.TryAddWithoutValidation("Cookie", cookie);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return results;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("req", out var root) ||
                !root.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("body", out var bodyEl) ||
                !bodyEl.TryGetProperty("album", out var albumWrap) ||
                albumWrap.ValueKind != JsonValueKind.Object ||
                !albumWrap.TryGetProperty("list", out var album) ||
                album.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var a in album.EnumerateArray())
            {
                var mid = GetString(a, "albumMID");
                if (string.IsNullOrEmpty(mid))
                    continue;
                var extra = GetString(a, "publicTime") ?? "";
                results.Add(new CoverSearchResult(
                    GetString(a, "albumName") ?? "",
                    GetString(a, "singerName") ?? "",
                    $"https://y.gtimg.cn/music/photo_new/T002R800x800M000{mid}.jpg",
                    $"https://y.gtimg.cn/music/photo_new/T002R180x180M000{mid}.jpg",
                    "QQ",
                    extra));
            }
        }
        catch
        {
            // offline / blocked / not logged in — empty result
        }
        return results;
    }

    // ---------- KuGou ----------

    /// <summary>KuGou album art in two hops: the HTTPS song search resolves
    /// the first hit's AlbumID, then the album-info endpoint returns an
    /// imgurl TEMPLATE — "stdmusic/{size}/..." — where {size} picks the
    /// rendition (150/240/480 verified live; 480 is the largest that works).
    /// mobilecdn (the other info host) has no TLS; mobiles does.</summary>
    private static async Task<List<CoverSearchResult>> SearchKugouAsync(string keyword, int limit)
    {
        var results = new List<CoverSearchResult>();
        try
        {
            var searchUrl = "https://songsearch.kugou.com/song_search_v2" +
                            $"?keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize={Math.Max(1, limit / 2)}";
            using var resp = await Http.GetAsync(searchUrl);
            if (!resp.IsSuccessStatusCode)
                return results;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("lists", out var lists) ||
                lists.ValueKind != JsonValueKind.Array)
                return results;

            // One album per distinct AlbumID, in relevance order. The field
            // arrives as a STRING ("39794135"), not a number.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hit in lists.EnumerateArray())
            {
                var albumId = hit.TryGetProperty("AlbumID", out var aid) &&
                              (aid.ValueKind == JsonValueKind.Number || aid.ValueKind == JsonValueKind.String)
                    ? aid.GetRawText().Trim('"')
                    : "";
                if (albumId.Length == 0 || albumId == "0" || !seen.Add(albumId))
                    continue;
                var albumName = GetString(hit, "AlbumName") ?? "";
                var singer = GetString(hit, "SingerName") ?? "";
                if (albumName.Length == 0)
                    continue;

                var imgTemplate = await KugouAlbumImgAsync(albumId);
                if (imgTemplate == null)
                    continue;
                var big = imgTemplate.Replace("{size}", "480").Replace("http://", "https://");
                var thumb = imgTemplate.Replace("{size}", "120").Replace("http://", "https://");
                results.Add(new CoverSearchResult(albumName, singer, big, thumb, "酷狗", ""));
                if (results.Count >= limit)
                    break;
            }
        }
        catch
        {
            // offline / blocked — empty result
        }
        return results;
    }

    private static async Task<string?> KugouAlbumImgAsync(string albumId)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://mobiles.kugou.com/api/v3/album/info?albumid={Uri.EscapeDataString(albumId)}");
            if (!resp.IsSuccessStatusCode)
                return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var d) ||
                !d.TryGetProperty("imgurl", out var img) ||
                img.ValueKind != JsonValueKind.String)
                return null;
            var url = img.GetString();
            return string.IsNullOrEmpty(url) || !url.Contains("{size}") ? null : url;
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

    private static async Task<List<CoverSearchResult>> SearchNetEaseAsync(string keyword, int limit)
    {
        var results = new List<CoverSearchResult>();
        try
        {
            // POST form endpoint (/api/search/get/). The GET variant
            // (/api/search/get/web?...) rate-limits aggressively under batch
            // load — returns code 405 "操作频繁" after ~100 quick requests —
            // while the form endpoint keeps serving and matches artist+album
            // keywords the GET variant returns nothing for.
            string? json = null;
            for (var attempt = 1; attempt <= 2 && json == null; attempt++)
            {
                try
                {
                    using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["s"] = keyword,
                        ["type"] = "10",
                        ["offset"] = "0",
                        ["limit"] = limit.ToString(),
                    });
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/search/get/")
                    { Content = content };
                    using var resp = await Http.SendAsync(req);
                    if (!resp.IsSuccessStatusCode)
                    {
                        if (attempt == 1) await Task.Delay(2500); // throttle — back off once and retry
                        continue;
                    }
                    json = await resp.Content.ReadAsStringAsync();
                }
                catch
                {
                    if (attempt == 1) await Task.Delay(2500);
                }
            }
            if (json == null)
                return results;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("albums", out var albums) ||
                albums.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var a in albums.EnumerateArray())
            {
                var pic = GetString(a, "picUrl");
                if (string.IsNullOrEmpty(pic))
                    continue;
                // picUrl sometimes arrives as http:// — the image server is https-ready.
                pic = pic.Replace("http://", "https://");
                var artist = "";
                foreach (var prop in new[] { "artist", "artists" })
                {
                    if (a.TryGetProperty(prop, out var ar) && ar.ValueKind == JsonValueKind.Array)
                    {
                        artist = string.Join("/",
                            ar.EnumerateArray().Select(x => GetString(x, "name")).Where(n => !string.IsNullOrEmpty(n)));
                        break;
                    }
                    if (a.TryGetProperty(prop, out var arObj) && arObj.ValueKind == JsonValueKind.Object)
                    {
                        artist = GetString(arObj, "name") ?? "";
                        break;
                    }
                }
                var extra = "";
                if (a.TryGetProperty("publishTime", out var pt) && pt.ValueKind == JsonValueKind.Number &&
                    pt.TryGetInt64(out var ms) && ms > 0)
                {
                    try { extra = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).Year.ToString(); }
                    catch { }
                }

                results.Add(new CoverSearchResult(
                    GetString(a, "name") ?? "",
                    artist,
                    pic + "?param=1000y1000",
                    pic + "?param=120y120",
                    "网易云",
                    extra));
            }
        }
        catch
        {
            // offline / blocked / malformed — empty result, caller falls through
        }
        return results;
    }

    private static async Task<List<CoverSearchResult>> SearchItunesAsync(string keyword, int limit)
    {
        // JP storefront first (CJK catalogues), CN second; dedupe by collection id.
        var jp = await SearchItunesCountryAsync(keyword, "JP", limit);
        var cn = await SearchItunesCountryAsync(keyword, "CN", limit);
        var seen = new HashSet<long>();
        var merged = new List<CoverSearchResult>();
        foreach (var r in jp.Concat(cn))
        {
            if (!seen.Add(r.CollectionId))
                continue;
            merged.Add(r.Result);
        }
        return merged;
    }

    private readonly record struct ItunesHit(long CollectionId, CoverSearchResult Result);

    private static async Task<List<ItunesHit>> SearchItunesCountryAsync(string keyword, string country, int limit)
    {
        var hits = new List<ItunesHit>();
        try
        {
            var url = "https://itunes.apple.com/search" +
                      $"?term={Uri.EscapeDataString(keyword)}&entity=album&limit={limit}&country={country}";
            using var resp = await Http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
                return hits;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return hits;

            foreach (var r in arr.EnumerateArray())
            {
                var art = GetString(r, "artworkUrl100");
                if (string.IsNullOrEmpty(art))
                    continue;
                // artworkUrl100 → largest available rendition.
                var big = art.Contains("100x100bb", StringComparison.OrdinalIgnoreCase)
                    ? art.Replace("100x100bb", "100000x100000bb", StringComparison.OrdinalIgnoreCase)
                    : art;

                var id = r.TryGetProperty("collectionId", out var cid) && cid.ValueKind == JsonValueKind.Number
                    ? cid.GetInt64() : 0;
                var extra = "";
                if (r.TryGetProperty("trackCount", out var tc) && tc.ValueKind == JsonValueKind.Number)
                    extra = $"{tc.GetInt32()} 曲";

                hits.Add(new ItunesHit(id, new CoverSearchResult(
                    GetString(r, "collectionName") ?? "",
                    GetString(r, "artistName") ?? "",
                    big,
                    art,
                    "iTunes",
                    extra)));
            }
        }
        catch
        {
            // offline / blocked — empty result
        }
        return hits;
    }

    // ---------- Download + embed ----------

    /// <summary>Download an image, capped at 20 MB. Returns (bytes, mime) or null.</summary>
    public static async Task<(byte[] Data, string Mime)?> DownloadAsync(string url)
    {
        try
        {
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode)
                return null;
            var len = resp.Content.Headers.ContentLength;
            if (len is > 20 * 1024 * 1024)
                return null;

            using var stream = await resp.Content.ReadAsStreamAsync();
            using var ms = new MemoryStream();
            var buf = new byte[64 * 1024];
            int read, total = 0;
            while ((read = await stream.ReadAsync(buf)) > 0)
            {
                total += read;
                if (total > 20 * 1024 * 1024)
                    return null;
                ms.Write(buf, 0, read);
            }
            var data = ms.ToArray();
            var mime = DetectMime(data) ?? GuessMimeFromUrl(url);
            return mime == null ? null : (data, mime);
        }
        catch
        {
            return null;
        }
    }

    private static string? DetectMime(byte[] d)
    {
        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return "image/jpeg";
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return "image/png";
        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' &&
            d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P') return "image/webp";
        return null;
    }

    private static string? GuessMimeFromUrl(string url)
    {
        var lower = url.ToLowerInvariant();
        if (lower.Contains(".png")) return "image/png";
        if (lower.Contains(".webp")) return "image/webp";
        if (lower.Contains(".jpg") || lower.Contains(".jpeg")) return "image/jpeg";
        return null;
    }

    /// <summary>True when the audio file already carries an embedded cover picture.</summary>
    public static bool HasEmbeddedCover(string audioPath)
    {
        try
        {
            using var f = TagLib.File.Create(audioPath);
            return f.Tag.Pictures is { Length: > 0 };
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Write <paramref name="data"/> into the audio file as its front-cover
    /// picture (REPLACING any existing pictures — the batch path checks
    /// HasEmbeddedCover first and skips; the manual path replaces on purpose).
    /// Read-only files (most copied libraries are) are temporarily unlocked
    /// for the write and restored afterwards. The caller refreshes the track's
    /// metadata afterwards.
    /// </summary>
    public static bool EmbedCover(string audioPath, byte[] data, string mime)
    {
        System.IO.FileAttributes attrs;
        try
        {
            attrs = System.IO.File.GetAttributes(audioPath);
        }
        catch
        {
            return false; // file vanished
        }
        var wasReadOnly = attrs.HasFlag(System.IO.FileAttributes.ReadOnly);
        try
        {
            if (wasReadOnly)
                System.IO.File.SetAttributes(audioPath, attrs & ~System.IO.FileAttributes.ReadOnly);

            using var f = TagLib.File.Create(audioPath);
            f.Tag.Pictures = new TagLib.IPicture[]
            {
                new TagLib.Picture(new TagLib.ByteVector(data))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mime,
                    Description = string.Empty,
                }
            };
            f.Save();
            return true;
        }
        catch
        {
            // unsupported tag type, decode error, locked file — caller reports failure
            return false;
        }
        finally
        {
            if (wasReadOnly)
            {
                try { System.IO.File.SetAttributes(audioPath, attrs); } catch { /* best effort */ }
            }
        }
    }
}
