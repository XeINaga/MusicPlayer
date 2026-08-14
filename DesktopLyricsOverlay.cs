using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
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
    private readonly object _gate = new();
    private bool _disposed;

    /// <summary>Launch the overlay process (if needed) and connect the pipe.</summary>
    private void EnsureStarted()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            bool alive = _proc != null && !_proc.HasExited;
            if (!alive)
            {
                _pipe = null;
                _proc?.Dispose();
                _proc = null;

                if (File.Exists(_exePath))
                {
                    try
                    {
                        _proc = new Process
                        {
                            StartInfo = new ProcessStartInfo(_exePath)
                            {
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            }
                        };
                        _proc.Start();
                    }
                    catch
                    {
                        _proc = null;
                    }
                }
            }

            if (_pipe == null && _proc != null && !_proc.HasExited)
            {
                // The C++ side creates its pipe on startup; retry until ready.
                for (int i = 0; i < 60; i++)
                {
                    try
                    {
                        var p = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                        p.Connect(150);
                        _pipe = p;
                        break;
                    }
                    catch
                    {
                        if (i == 59)
                            break;
                        Thread.Sleep(50);
                    }
                }
            }
        }
    }

    /// <summary>Send one newline-terminated UTF-8 JSON command to the overlay.</summary>
    private void Send(string json)
    {
        EnsureStarted();
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

    /// <summary>Apply the desktop-lyrics visual style (font / color / opacity / alignment).</summary>
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
        };
        Send(JsonSerializer.Serialize(payload, JsonOptions));
    }

    /// <summary>Toggle click-through (mouse passes through to the desktop).</summary>
    public void SetClickThrough(bool on) =>
        Send(JsonSerializer.Serialize(new { t = "click", on = on ? 1 : 0 }, JsonOptions));

    /// <summary>Push the current lyric line (original / romaji / translation).</summary>
    public void UpdateLyric(MusicPlayer.Models.Track? track, string original, string? romaji, string? translation)
    {
        Send(JsonSerializer.Serialize(new
        {
            t = "lyric",
            orig = original ?? "",
            roma = romaji ?? "",
            trans = translation ?? "",
        }, JsonOptions));
    }

    /// <summary>Show the overlay window (also lazily starts the process).</summary>
    public void Activate()
    {
        EnsureStarted();
        Send(JsonSerializer.Serialize(new { t = "show" }, JsonOptions));
    }

    /// <summary>Hide and terminate the overlay process.</summary>
    public void Close()
    {
        Send(JsonSerializer.Serialize(new { t = "hide" }, JsonOptions));
        Send(JsonSerializer.Serialize(new { t = "quit" }, JsonOptions));

        lock (_gate)
        {
            try { _pipe?.Dispose(); } catch { }
            _pipe = null;
            if (_proc != null && !_proc.HasExited)
            {
                try { _proc.Kill(); } catch { }
            }
            _proc?.Dispose();
            _proc = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Close();
    }
}
