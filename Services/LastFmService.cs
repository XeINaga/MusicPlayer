using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>
/// Last.fm scrobbling service: authentication (token → browser auth → session),
/// and track.scrobble with batch support (≤50 tracks per call). Failed scrobbles
/// are queued to disk and retried on next launch.
/// </summary>
public sealed class LastFmService
{
    private const string ApiBase = "https://ws.audioscrobbler.com/2.0/";
    private static readonly HttpClient Http = CreateClient();

    private readonly AppSettings _settings;

    /// <summary>Raised on the UI thread when a scrobble succeeds or fails.</summary>
    public event Action<string>? StatusChanged;

    public LastFmService(AppSettings settings)
    {
        _settings = settings;
    }

    // Same factory pattern as the lyric services (see QQLyricService): the
    // default HttpClient would time out only after 100 s and hold pooled
    // connections forever.
    private static HttpClient CreateClient()
    {
        var c = new HttpClient(new SocketsHttpHandler
        {
            // Long-running desktop app: drop pooled connections so a network /
            // DNS change recovers on the next request instead of serving
            // stale sockets until restart.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        });
        c.Timeout = TimeSpan.FromSeconds(15);
        return c;
    }

    // ───────────── Authentication ─────────────

    /// <summary>Step 1: obtain a temporary request token from Last.fm.</summary>
    public async Task<string?> GetTokenAsync()
    {
        if (string.IsNullOrEmpty(_settings.LastFmApiKey))
            return null;

        var url = $"{ApiBase}?method=auth.getToken&api_key={_settings.LastFmApiKey}&format=json";
        var json = await Http.GetStringAsync(url);
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("token", out var tokenEl))
            return tokenEl.GetString();
        return null;
    }

    /// <summary>Returns the URL the user must visit to authorize the app.</summary>
    public string GetAuthUrl(string token) =>
        $"https://www.last.fm/auth/?api_key={_settings.LastFmApiKey}&token={token}";

    /// <summary>
    /// Step 2: exchange the authorized token for a session key + username.
    /// The api_sig is MD5 of all sorted params concatenated as key+value, plus the shared secret.
    /// </summary>
    public async Task<(string SessionKey, string Username)?> GetSessionAsync(string token)
    {
        if (string.IsNullOrEmpty(_settings.LastFmApiKey))
            return null;

        var parameters = new SortedDictionary<string, string>
        {
            ["api_key"] = _settings.LastFmApiKey,
            ["method"] = "auth.getSession",
            ["token"] = token
        };
        var sig = ComputeApiSig(parameters);

        var qs = string.Join("&", parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        var url = $"{ApiBase}?{qs}&api_sig={sig}&format=json";

        var json = await Http.GetStringAsync(url);
        var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("session", out var session))
        {
            var key = session.TryGetProperty("key", out var k) ? k.GetString() : null;
            var name = session.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!string.IsNullOrEmpty(key))
            {
                _settings.LastFmSessionKey = key!;
                _settings.LastFmUsername = name ?? "";
                SettingsStore.Save(_settings);
                StatusChanged?.Invoke($"Last.fm 已连接：{_settings.LastFmUsername}");
                return (key!, _settings.LastFmUsername);
            }
        }

        return null;
    }

    /// <summary>Disconnect from Last.fm (clear stored session).</summary>
    public void Disconnect()
    {
        _settings.LastFmSessionKey = "";
        _settings.LastFmUsername = "";
        SettingsStore.Save(_settings);
        ClearFailedScrobbles();
        StatusChanged?.Invoke("Last.fm 已断开");
    }

    public bool IsConnected => !string.IsNullOrEmpty(_settings.LastFmSessionKey);
    public string Username => _settings.LastFmUsername;

    // ───────────── Scrobbling ─────────────

    /// <summary>
    /// Scrobble a batch of tracks (max 50 per Last.fm spec). Each item
    /// contains the track metadata + the timestamp when it started playing.
    /// </summary>
    public async Task ScrobbleBatchAsync(IList<ScrobbleEntry> entries)
    {
        if (!IsConnected || entries.Count == 0)
            return;

        var serverRejected = 0;
        // Split into chunks of 50
        for (var i = 0; i < entries.Count; i += 50)
        {
            var chunk = entries.Skip(i).Take(50).ToList();
            var (delivered, rejected) = await PostScrobbleChunkAsync(chunk);
            if (!delivered)
            {
                // Queue the failed chunk for retry
                SaveFailedScrobbles(chunk);
                StatusChanged?.Invoke("Last.fm scrobble 失败，将在下次启动时重试");
                return;
            }
            serverRejected += rejected.Count;
        }

        StatusChanged?.Invoke(serverRejected > 0
            ? $"Last.fm 已 scrobble {entries.Count - serverRejected} 首，拒收 {serverRejected} 首（时间戳过老或元数据不全）"
            : $"Last.fm 已 scrobble {entries.Count} 首歌曲");
    }

    /// <summary>Retry any previously failed scrobbles (called on app startup).</summary>
    public async Task RetryFailedScrobblesAsync()
    {
        var pending = LoadFailedScrobbles();
        if (pending.Count == 0)
            return;

        // The backlog accumulates across failures, so it can hold far more than
        // the 50 entries Last.fm accepts per request. Posting it whole always
        // fails, which means ClearFailedScrobbles() never runs and every startup
        // re-posts the entire pile forever. Walk it in chunks and keep only what
        // is still outstanding.
        var done = 0;
        var serverRejected = 0;
        for (var i = 0; i < pending.Count; i += 50)
        {
            var chunk = pending.Skip(i).Take(50).ToList();
            var (delivered, rejected) = await PostScrobbleChunkAsync(chunk);
            if (!delivered)
            {
                // This chunk and everything after it stay queued.
                WriteFailedScrobbles(pending.Skip(i).ToList());
                if (done > 0)
                    StatusChanged?.Invoke($"Last.fm 重试 scrobble 部分成功：{done} 首");
                return;
            }
            done += chunk.Count - rejected.Count;
            serverRejected += rejected.Count;
        }

        ClearFailedScrobbles();
        StatusChanged?.Invoke(serverRejected > 0
            ? $"Last.fm 重试 scrobble 成功：{done} 首，拒收 {serverRejected} 首（已跳过）"
            : $"Last.fm 重试 scrobble 成功：{done} 首");
    }

    /// <summary>
    /// Post one chunk of scrobbles.
    /// Returns (delivered, serverRejected): delivered=false means the request
    /// itself failed (network/session/parse) and the WHOLE chunk should be
    /// retried; delivered=true means the server processed it, and
    /// serverRejected lists the individual entries Last.fm refused. Refusals
    /// are permanent (timestamp too old, missing metadata), so the caller
    /// drops them with a status report instead of re-queueing — the old
    /// "accepted > 0" check silently lost them, and re-queueing would just
    /// re-send the same refused entries on every launch forever.
    /// </summary>
    private async Task<(bool Delivered, List<ScrobbleEntry> ServerRejected)> PostScrobbleChunkAsync(
        IList<ScrobbleEntry> entries)
    {
        if (!IsConnected)
            return (false, new List<ScrobbleEntry>());

        try
        {
            var parameters = new SortedDictionary<string, string>
            {
                ["api_key"] = _settings.LastFmApiKey!,
                ["method"] = "track.scrobble",
                ["sk"] = _settings.LastFmSessionKey!
            };

            // Add numbered params for each track (up to 50)
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                parameters[$"artist[{i}]"] = e.Artist ?? "未知歌手";
                parameters[$"track[{i}]"] = e.Track ?? "";
                parameters[$"timestamp[{i}]"] = e.TimestampUnix.ToString();
                if (!string.IsNullOrEmpty(e.Album))
                    parameters[$"album[{i}]"] = e.Album;
                if (e.Duration > 0)
                    parameters[$"duration[{i}]"] = e.Duration.ToString();
            }

            var sig = ComputeApiSig(parameters);
            parameters["api_sig"] = sig;
            parameters["format"] = "json";

            var content = new FormUrlEncodedContent(parameters.Select(p =>
                new KeyValuePair<string, string>(p.Key, p.Value)));

            var response = await Http.PostAsync(ApiBase, content);
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            // Last.fm returns accepted/rejected counts plus a per-track
            // scrobble array whose ignoredMessage.code is non-zero for the
            // refused entries.
            if (doc.RootElement.TryGetProperty("scrobbles", out var scrobbles) &&
                scrobbles.TryGetProperty("@attr", out var attr))
            {
                var rejectedTs = new HashSet<long>();
                if (scrobbles.TryGetProperty("scrobble", out var perTrack))
                {
                    // Single-entry responses come back as an object, batches
                    // as an array — normalize both.
                    if (perTrack.ValueKind == JsonValueKind.Object)
                        CollectRejectedTimestamp(perTrack, rejectedTs);
                    else if (perTrack.ValueKind == JsonValueKind.Array)
                        foreach (var s in perTrack.EnumerateArray())
                            CollectRejectedTimestamp(s, rejectedTs);
                }
                var rejected = entries.Where(e => rejectedTs.Contains(e.TimestampUnix)).ToList();
                return (true, rejected);
            }

            // Parsed but no scrobbles block — treat an HTTP error as
            // undelivered (retry), a 2xx without the block as delivered.
            return (response.IsSuccessStatusCode, new List<ScrobbleEntry>());
        }
        catch
        {
            return (false, new List<ScrobbleEntry>());
        }
    }

    /// <summary>Add the timestamp of a refused scrobble entry (ignoredMessage
    /// code non-zero) to <paramref name="into"/>. Timestamp arrives as a
    /// string or a number depending on the response shape.</summary>
    private static void CollectRejectedTimestamp(JsonElement s, HashSet<long> into)
    {
        var code = s.TryGetProperty("ignoredMessage", out var im) &&
                   im.TryGetProperty("code", out var c)
            ? c.GetString()
            : null;
        if (string.IsNullOrEmpty(code) || code == "0")
            return;
        if (!s.TryGetProperty("timestamp", out var ts))
            return;
        if (ts.ValueKind == JsonValueKind.String && long.TryParse(ts.GetString(), out var t))
            into.Add(t);
        else if (ts.ValueKind == JsonValueKind.Number && ts.TryGetInt64(out var n))
            into.Add(n);
    }

    // ───────────── Failed scrobble persistence ─────────────

    // Resolved per access so a runtime DataLocation change takes effect.
    private static string FailedScrobblePath =>
        Path.Combine(DataLocation.Root, "lastfm_pending.json");

    private void SaveFailedScrobbles(IList<ScrobbleEntry> entries)
    {
        var existing = LoadFailedScrobbles();
        existing.AddRange(entries);
        // Cap at 500 to prevent unbounded growth
        if (existing.Count > 500)
            existing = existing.TakeLast(500).ToList();
        WriteFailedScrobbles(existing);
    }

    /// <summary>
    /// Overwrite the pending list outright, as opposed to
    /// <see cref="SaveFailedScrobbles"/> which appends to whatever is there.
    /// Needed by the retry path so successfully scrobbled entries drop out.
    /// </summary>
    private static void WriteFailedScrobbles(IList<ScrobbleEntry> entries)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FailedScrobblePath)!);
            File.WriteAllText(FailedScrobblePath,
                JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort
        }
    }

    private static List<ScrobbleEntry> LoadFailedScrobbles()
    {
        try
        {
            if (File.Exists(FailedScrobblePath))
            {
                var json = File.ReadAllText(FailedScrobblePath);
                return JsonSerializer.Deserialize<List<ScrobbleEntry>>(json) ?? new();
            }
        }
        catch
        {
            // fall through
        }
        return new();
    }

    private static void ClearFailedScrobbles()
    {
        try { if (File.Exists(FailedScrobblePath)) File.Delete(FailedScrobblePath); }
        catch { /* best-effort */ }
    }

    // ───────────── API signature ─────────────

    /// <summary>
    /// Compute Last.fm api_sig: sort all params alphabetically by key,
    /// concatenate key+value pairs, append the shared secret, MD5 hash.
    /// </summary>
    private string ComputeApiSig(SortedDictionary<string, string> parameters)
    {
        var sb = new StringBuilder();
        foreach (var kv in parameters)
        {
            if (kv.Key == "format" || kv.Key == "callback" || kv.Value == "")
                continue; // format/callback are not signed; empty values are excluded
            sb.Append(kv.Key);
            sb.Append(kv.Value);
        }
        sb.Append(_settings.LastFmApiSecret);
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

/// <summary>A single scrobble submission.</summary>
public sealed class ScrobbleEntry
{
    public string? Artist { get; set; }
    public string? Track { get; set; }
    public string? Album { get; set; }
    public long TimestampUnix { get; set; }   // Unix epoch seconds
    public int Duration { get; set; }          // seconds
}
