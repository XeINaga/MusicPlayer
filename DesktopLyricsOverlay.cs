using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MusicPlayer.Services;

namespace MusicPlayer;

/// <summary>
/// Client side of the desktop-lyrics overlay.
///
/// The actual transparent window lives in a small standalone C++ process
/// (<c>DesktopLyricsOverlay.exe</c>, a Win32 layered window with
/// Direct2D/DirectWrite text). WinUI 3 cannot produce a truly transparent
/// content window in this environment (its swap chain is opaque to the
/// desktop and <c>AppWindow.TransparencyKind</c> is absent from every
/// available Windows App SDK build), so the overlay is a separate process
/// that this class drives over a named pipe.
///
/// The public surface mirrors the old in-process <c>DesktopLyricsWindow</c>
/// so <see cref="MainWindow"/> barely changes.
/// </summary>
public sealed class DesktopLyricsOverlay : IDisposable
{
    private static readonly string PipeName = "MusicPlayerDesktopLyrics";
    // Dedicated pipe for overlay -> host position reports (one direction,
    // one thread each side — the main pipe must never be written from the
    // overlay side while its reader thread blocks in ReadFile).
    private static readonly string RptPipeName = "MusicPlayerDesktopLyricsRpt";
    private readonly string _exePath = Path.Combine(AppContext.BaseDirectory, "DesktopLyricsOverlay.exe");

    // Send non-ASCII (Chinese/Japanese/emoji) verbatim as UTF-8 instead of
    // \uXXXX escapes — smaller payload and avoids the overlay mis-rendering
    // literal "uXXXX" text (it now also decodes \uXXXX defensively).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private Process? _proc;
    private NamedPipeClientStream? _pipe;
    private NamedPipeClientStream? _pipeRpt;
    private readonly object _gate = new();
    // FIFO send queue: SemaphoreSlim wakeups don't preserve order, so a
    // burst of style/click/pos/show/lyric commands could reach the overlay
    // reordered. A single-reader Channel guarantees it.
    private readonly System.Threading.Channels.Channel<string> _sendChannel =
        System.Threading.Channels.Channel.CreateUnbounded<string>(
            new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true });
    private Task? _sendLoop;
    private bool _disposed;

    /// <summary>The overlay reports its window position (drag end / quit)
    /// as {"t":"pos","x":…,"y":…} — raised on a background thread.</summary>
    public event Action<int, int>? PositionReported;

    /// <summary>The overlay reports the user-set wrap width (resize end)
    /// as {"t":"size","w":…} — raised on a background thread.</summary>
    public event Action<int>? SizeReported;

    /// <summary>
    /// Launch the overlay process (if needed) and connect both pipes.
    /// Runs on the send worker thread only (serialized by the semaphore), so
    /// the long connect retries live here — every _gate section below stays
    /// short enough that the UI thread's Close() can never get stuck on it.
    /// </summary>
    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            if (_proc != null && !_proc.HasExited && _pipe != null && _pipeRpt != null)
                return; // already up

            try { _pipe?.Dispose(); } catch { }
            try { _pipeRpt?.Dispose(); } catch { }
            _pipe = null;
            _pipeRpt = null;
            if (_proc != null && _proc.HasExited)
            {
                _proc.Dispose();
                _proc = null;
            }

            if (_proc == null && File.Exists(_exePath))
            {
                try
                {
                    _proc = new Process
                    {
                        StartInfo = new ProcessStartInfo(_exePath)
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            // The overlay rejects pipe clients whose process id
                            // differs from the host (us).
                            Arguments = $"--host-pid {Environment.ProcessId}",
                        }
                    };
                    _proc.Start();
                }
                catch
                {
                    _proc = null;
                }
            }

            if (_proc == null || _proc.HasExited)
            {
                if (_disposed && _proc != null)
                {
                    try { _proc.Kill(); } catch { }
                    _proc.Dispose();
                    _proc = null;
                }
                return;
            }
        }

        // The C++ side creates both pipes on startup; retry until ready.
        // Kept OUTSIDE any lock so Close() on the UI thread never blocks here.
        NamedPipeClientStream? main = null, rpt = null;
        for (int i = 0; i < 60; i++)
        {
            if (_disposed)
                break;

            if (main == null)
            {
                try { main = new NamedPipeClientStream(".", PipeName, PipeDirection.Out); main.Connect(150); }
                catch { try { main!.Dispose(); } catch { } main = null; }
            }
            if (rpt == null)
            {
                try { rpt = new NamedPipeClientStream(".", RptPipeName, PipeDirection.In); rpt.Connect(150); }
                catch { try { rpt!.Dispose(); } catch { } rpt = null; }
            }
            if (main != null && rpt != null)
                break;

            Thread.Sleep(50);
        }

        if (main == null || rpt == null)
        {
            try { main?.Dispose(); } catch { }
            try { rpt?.Dispose(); } catch { }
            lock (_gate)
            {
                KillOrphan(_proc);
                _proc = null;
            }
            return;
        }

        lock (_gate)
        {
            if (_disposed)
            {
                try { main.Dispose(); } catch { }
                try { rpt.Dispose(); } catch { }
                KillOrphan(_proc);
                _proc = null;
                return;
            }
            _pipe = main;
            _pipeRpt = rpt;
        }

        StartReadLoop(rpt);
    }

    /// <summary>Kill a freshly spawned overlay that nobody took ownership of
    /// (a Close() raced the connect loop) so it cannot linger as a zombie.</summary>
    private static void KillOrphan(Process? proc)
    {
        if (proc == null) return;
        try { if (!proc.HasExited) proc.Kill(); } catch { }
        try { proc.Dispose(); } catch { }
    }

    /// <summary>Background reader for position reports (exits when the pipe dies).</summary>
    private void StartReadLoop(NamedPipeClientStream pipe)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            var buf = new byte[512];
            var acc = new System.Text.StringBuilder();
            try
            {
                while (true)
                {
                    var n = pipe.Read(buf, 0, buf.Length);
                    if (n <= 0)
                        break;
                    acc.Append(Encoding.UTF8.GetString(buf, 0, n));
                    while (true)
                    {
                        var line = acc.ToString();
                        var nl = line.IndexOf('\n');
                        if (nl < 0)
                            break;
                        var msg = line[..nl].Trim();
                        acc.Remove(0, nl + 1);
                        HandleReport(msg);
                    }
                }
            }
            catch
            {
                // pipe closed/disposed — reader exits
            }
        });
    }

    private void HandleReport(string msg)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(msg);
            var root = doc.RootElement;
            if (root.TryGetProperty("t", out var t))
            {
                if (t.GetString() == "pos"
                    && root.TryGetProperty("x", out var x) && x.TryGetInt32(out var xi)
                    && root.TryGetProperty("y", out var y) && y.TryGetInt32(out var yi))
                {
                    PositionReported?.Invoke(xi, yi);
                }
                else if (t.GetString() == "size"
                    && root.TryGetProperty("w", out var w) && w.TryGetInt32(out var wi))
                {
                    SizeReported?.Invoke(wi);
                }
            }
        }
        catch
        {
            // ignore malformed reports
        }
    }

    /// <summary>Restore the overlay to a persisted position.</summary>
    public void SetPosition(int x, int y) =>
        Send(JsonSerializer.Serialize(new { t = "pos", x, y }, JsonOptions));

    /// <summary>
    /// Send one newline-terminated UTF-8 JSON command to the overlay.
    /// Runs on the thread pool (serialized) so the connection retries can never
    /// block the UI thread.
    /// </summary>
    private void Send(string json)
    {
        if (_disposed)
            return;
        EnsureSendLoop();
        _sendChannel.Writer.TryWrite(json);
    }

    private void EnsureSendLoop()
    {
        if (_sendLoop != null)
            return;
        _sendLoop = Task.Run(async () =>
        {
            var reader = _sendChannel.Reader;
            while (await reader.WaitToReadAsync())
            {
                while (reader.TryRead(out var json))
                {
                    try
                    {
                        EnsureStarted();
                        WriteToPipe(json);
                    }
                    catch (ObjectDisposedException)
                    {
                        // overlay closed mid-drain — drop the rest
                        return;
                    }
                    catch (Exception ex)
                    {
                        // Surface instead of an unobserved task exception.
                        AppLog.WriteLyricCompletion($"桌面歌词命令发送失败: {ex.Message}");
                    }
                }
            }
        });
    }

    /// <summary>Write to the already-connected pipe only — never starts anything.</summary>
    private void WriteToPipe(string json)
    {
        lock (_gate)
        {
            if (_pipe == null)
                return;
            try
            {
                var bytes = Encoding.UTF8.GetBytes(json + "\n");
                _pipe.Write(bytes, 0, bytes.Length);
                _pipe.Flush();
            }
            catch
            {
                // Broken pipe: drop it so the next Send reconnects / restarts.
                try { _pipe.Dispose(); } catch { }
                _pipe = null;
            }
        }
    }

    /// <summary>Apply the desktop-lyrics visual style (font / color / opacity / alignment / line order).</summary>
    public void ApplyStyle(AppSettings s)
    {
        var payload = new
        {
            t = "style",
            font = s.LyricFontSize,
            color = s.LyricColor ?? "#FFFFFFFF",
            bg = s.LyricBgOpacity,
            bold = s.LyricBold ? 1 : 0,
            align = s.LyricAlign ?? "Center",
            order = LyricPreferences.ParseLineOrder(s.LyricLineOrder),
            vertical = s.LyricVertical ? 1 : 0,
            width = s.LyricBoxWidth,
            accent = s.LyricSungColor ?? "#31C27C",
            unsung = s.LyricUnsungColor ?? "#FFFFFF",
        };
        Send(JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>Toggle click-through (mouse passes through to the desktop).</summary>
    public void SetClickThrough(bool on) =>
        Send(JsonSerializer.Serialize(new { t = "click", on = on ? 1 : 0 }, JsonOptions));

    /// <summary>Push the current lyric line (original / romaji / translation).</summary>
    public void UpdateLyric(MusicPlayer.Models.Track? track, string original, string? romaji, string? translation, double progress = -1)
    {
        // progress: continuous karaoke position of the current word-timed line
        // (0..1, interpolated inside words; -1 = not word-timed). The overlay
        // clips ALL lines (original/romaji/translation) to this fraction.
        Send(JsonSerializer.Serialize(new
        {
            t = "lyric",
            orig = original ?? "",
            roma = romaji ?? "",
            trans = translation ?? "",
            prog = progress,
        }, JsonOptions));
    }

    /// <summary>Show the overlay window (also lazily starts the process).</summary>
    public void Activate()
    {
        Send(JsonSerializer.Serialize(new { t = "show" }, JsonOptions));
    }

    /// <summary>Hide and terminate the overlay process (idempotent).</summary>
    public void Close()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sendChannel.Writer.TryComplete(); // drain-and-exit the send loop

        // Write directly to an existing pipe only — going through Send() would
        // spin up a NEW overlay process just to tell it to quit.
        WriteToPipe(JsonSerializer.Serialize(new { t = "hide" }, JsonOptions));
        WriteToPipe(JsonSerializer.Serialize(new { t = "quit" }, JsonOptions));

        lock (_gate)
        {
            try { _pipe?.Dispose(); } catch { }
            try { _pipeRpt?.Dispose(); } catch { }
            _pipe = null;
            _pipeRpt = null;
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.Kill(); } catch { }
            }
            _proc?.Dispose();
            _proc = null;
        }
    }

    public void Dispose() => Close();
}
