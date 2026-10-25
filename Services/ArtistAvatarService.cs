using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MusicPlayer.Services;

/// <summary>
/// Artist avatar fetch + cache for the artists grid.
/// Source chain (fixed order per product decision): QQ → NetEase → KuGou.
///   QQ     : musicu.fcg search_type=1 (singers) → singerMID →
///            y.gtimg.cn T001R800x800M000{mid}.jpg (verified 800², ~190 KB)
///   NetEase: /api/search/get/web type=100 → artists[0].img1v1Url (https)
///   KuGou  : songsearch song search → SingerId (STRING) →
///            mobiles v5 singer/info → imgurl template "{size}" → 400
/// Avatars are cached under {DataRoot}\avatars\{sha1(artist)}.jpg|.png keyed
/// by the grid's artist key, so a warm start never hits the network again.
/// </summary>
public static class ArtistAvatarService
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
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    private static string AvatarDir => Path.Combine(DataLocation.Root, "avatars");

    private static string HashKey(string artist)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(artist.Trim()));
        var sb = new StringBuilder(40);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>The cached avatar file for an artist, or null when none.</summary>
    public static string? GetAvatarFile(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return null;
        var baseName = Path.Combine(AvatarDir, HashKey(artist));
        foreach (var ext in new[] { ".jpg", ".png", ".webp" })
            if (File.Exists(baseName + ext))
                return baseName + ext;
        return null;
    }

    public static bool HasAvatar(string artist) => GetAvatarFile(artist) != null;

    public static void DeleteAvatar(string artist)
    {
        var baseName = Path.Combine(AvatarDir, HashKey(artist));
        foreach (var ext in new[] { ".jpg", ".png", ".webp" })
            try { if (File.Exists(baseName + ext)) File.Delete(baseName + ext); } catch { }
    }

    /// <summary>Persist downloaded/local bytes for an artist. The mime decides
    /// the extension (jpg/png/webp; unknown falls back to jpg).</summary>
    public static void SaveAvatar(string artist, byte[] data, string? mime)
    {
        try
        {
            Directory.CreateDirectory(AvatarDir);
            var ext = mime switch
            {
                "image/png" => ".png",
                "image/webp" => ".webp",
                _ => ".jpg",
            };
            // A different extension may already hold an older avatar — wipe it
            // so GetAvatarFile doesn't serve the stale one.
            DeleteAvatar(artist);
            File.WriteAllBytes(Path.Combine(AvatarDir, HashKey(artist) + ext), data);
        }
        catch
        {
            // cache write is best-effort; the avatar is re-fetchable
        }
    }

    public const int SourceAuto = 0, SourceQq = 1, SourceNetEase = 2, SourceKugou = 3;

    /// <summary>Search singer avatars across sources. Reuses the shared
    /// CoverSearchResult shape: Album carries the SINGER name, Extra the
    /// song/album count when the source provides one.</summary>
    public static async Task<List<CoverSearchResult>> SearchAsync(
        string artist, int source, int limit = 20, IReadOnlyList<int>? order = null)
    {
        if (string.IsNullOrWhiteSpace(artist))
            return new List<CoverSearchResult>();
        var keyword = artist.Trim();
        if (keyword.Length > 60)
            keyword = keyword[..60];

        if (source == SourceQq)
            return await SearchQqAsync(keyword, limit);
        if (source == SourceNetEase)
            return await SearchNetEaseAsync(keyword, limit);
        if (source == SourceKugou)
            return await SearchKugouAsync(keyword, limit);

        foreach (var s in order ?? new[] { SourceQq, SourceNetEase, SourceKugou })
        {
            var found = s switch
            {
                SourceNetEase => await SearchNetEaseAsync(keyword, limit),
                SourceKugou => await SearchKugouAsync(keyword, limit),
                _ => await SearchQqAsync(keyword, limit),
            };
            if (found.Count > 0)
                return found;
        }
        return new List<CoverSearchResult>();
    }

    /// <summary>Fetch an avatar through the fixed QQ → NetEase → KuGou chain.
    /// Returns null when no source produced a usable image.</summary>
    public static async Task<(byte[] Data, string Mime)?> FetchAsync(string artist)
    {
        var hits = await SearchAsync(artist, SourceAuto);
        foreach (var hit in hits)
        {
            var dl = await CoverArtService.DownloadAsync(hit.PicUrl);
            if (dl != null)
                return dl;
        }
        return null;
    }

    // ---------- QQ ----------

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
                    param = new { search_type = 1, query = keyword, page_num = 1, num_per_page = limit }
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
                !bodyEl.TryGetProperty("singer", out var singerWrap) ||
                singerWrap.ValueKind != JsonValueKind.Object ||
                !singerWrap.TryGetProperty("list", out var list) ||
                list.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var sg in list.EnumerateArray())
            {
                var mid = GetString(sg, "singerMID");
                if (string.IsNullOrEmpty(mid))
                    continue;
                var extra = "";
                if (sg.TryGetProperty("songNum", out var sn) && sn.ValueKind == JsonValueKind.Number)
                    extra = $"{sn.GetInt32()} 首";
                results.Add(new CoverSearchResult(
                    GetString(sg, "singerName") ?? "",
                    "",
                    $"https://y.gtimg.cn/music/photo_new/T001R800x800M000{mid}.jpg",
                    $"https://y.gtimg.cn/music/photo_new/T001R150x150M000{mid}.jpg",
                    "QQ",
                    extra));
            }
        }
        catch
        {
            return new List<CoverSearchResult>();
        }
        return results;
    }

    // ---------- NetEase ----------

    private static async Task<List<CoverSearchResult>> SearchNetEaseAsync(string keyword, int limit)
    {
        var results = new List<CoverSearchResult>();
        try
        {
            // POST form endpoint — the GET /web variant rate-limits (405)
            // exactly like the album search did.
            string? json = null;
            for (var attempt = 1; attempt <= 2 && json == null; attempt++)
            {
                try
                {
                    using var content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["s"] = keyword,
                        ["type"] = "100",
                        ["offset"] = "0",
                        ["limit"] = limit.ToString(),
                    });
                    using var req = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/search/get/")
                    { Content = content };
                    using var resp2 = await Http.SendAsync(req);
                    if (!resp2.IsSuccessStatusCode)
                    {
                        if (attempt == 1) await Task.Delay(2500);
                        continue;
                    }
                    json = await resp2.Content.ReadAsStringAsync();
                }
                catch
                {
                    if (attempt == 1) await Task.Delay(2500);
                }
            }
            if (json == null)
                return results;
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var r) ||
                !r.TryGetProperty("artists", out var artists) ||
                artists.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var a in artists.EnumerateArray())
            {
                // GET /web returns img1v1Url; the POST form endpoint returns
                // picUrl — accept either.
                var img = GetString(a, "img1v1Url") ?? GetString(a, "picUrl");
                if (string.IsNullOrEmpty(img))
                    continue;
                var extra = "";
                if (a.TryGetProperty("albumSize", out var asz) && asz.ValueKind == JsonValueKind.Number)
                    extra = $"{asz.GetInt32()} 专辑";
                results.Add(new CoverSearchResult(
                    GetString(a, "name") ?? "",
                    "",
                    img.Replace("http://", "https://"),
                    img.Replace("http://", "https://") + "?param=120y120",
                    "网易云",
                    extra));
            }
        }
        catch
        {
            return new List<CoverSearchResult>();
        }
        return results;
    }

    // ---------- KuGou ----------

    private static async Task<List<CoverSearchResult>> SearchKugouAsync(string keyword, int limit)
    {
        var results = new List<CoverSearchResult>();
        try
        {
            var searchUrl = "https://songsearch.kugou.com/song_search_v2" +
                            $"?keyword={Uri.EscapeDataString(keyword)}&page=1&pagesize={Math.Max(1, limit)}";
            using var resp = await Http.GetAsync(searchUrl);
            if (!resp.IsSuccessStatusCode)
                return results;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("lists", out var lists) ||
                lists.ValueKind != JsonValueKind.Array)
                return results;

            // Song hits resolve singer ids; one avatar per distinct id.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var hit in lists.EnumerateArray())
            {
                if (!hit.TryGetProperty("SingerId", out var sid))
                    continue;
                // SingerId arrives as an ARRAY ([754920]) — take the first
                // element; bare number/string shapes tolerated too.
                string? singerId = null;
                if (sid.ValueKind == JsonValueKind.Array && sid.GetArrayLength() > 0)
                    sid = sid[0];
                if (sid.ValueKind == JsonValueKind.Number)
                    singerId = sid.GetRawText();
                else if (sid.ValueKind == JsonValueKind.String)
                    singerId = sid.GetString();
                if (string.IsNullOrEmpty(singerId) || singerId == "0" || !seen.Add(singerId))
                    continue;
                var singerName = GetString(hit, "SingerName") ?? "";
                if (singerName.Length == 0)
                    continue;

                var template = await KugouAvatarTemplateAsync(singerId);
                if (template == null)
                    continue;
                results.Add(new CoverSearchResult(
                    singerName,
                    "",
                    template.Replace("{size}", "400").Replace("http://", "https://"),
                    template.Replace("{size}", "120").Replace("http://", "https://"),
                    "酷狗",
                    ""));
                if (results.Count >= limit)
                    break;
            }
        }
        catch
        {
            return new List<CoverSearchResult>();
        }
        return results;
    }

    private static async Task<string?> KugouAvatarTemplateAsync(string singerId)
    {
        try
        {
            using var resp = await Http.GetAsync(
                $"https://mobiles.kugou.com/api/v5/singer/info?singerid={Uri.EscapeDataString(singerId)}");
            if (!resp.IsSuccessStatusCode)
                return null;
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var d) ||
                !d.TryGetProperty("imgurl", out var img) ||
                img.ValueKind != JsonValueKind.String)
                return null;
            var template = img.GetString();
            return string.IsNullOrEmpty(template) || !template.Contains("{size}") ? null : template;
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

    // ---------- Startup warm-up ----------

    /// <summary>
    /// Background warm-up for every artist without a cached avatar. Called on
    /// every launch — artists that already have an avatar cost nothing, so
    /// the "every startup" requirement never re-hits the network for them.
    /// Two concurrent fetches; <paramref name="onUpdated"/> fires on a worker
    /// thread per newly cached artist (caller marshals to the UI itself).
    /// </summary>
    public static void WarmMissing(IEnumerable<string> artists, Action<string>? onUpdated)
    {
        _ = Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(2);
            var tasks = new List<Task>();
            foreach (var name in artists.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (HasAvatar(name))
                    continue; // cached — no network, per the every-startup contract
                var artist = name;
                await gate.WaitAsync();
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        var got = FetchAsync(artist).GetAwaiter().GetResult();
                        if (got != null)
                        {
                            SaveAvatar(artist, got.Value.Data, got.Value.Mime);
                            onUpdated?.Invoke(artist);
                        }
                    }
                    catch
                    {
                        // best effort — next launch retries
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));
            }
            await Task.WhenAll(tasks.ToArray());
        });
    }
}
