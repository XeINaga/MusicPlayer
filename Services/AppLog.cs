using System;
using System.IO;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>
/// Central log-directory resolution + lightweight append-only log writers.
///
/// The log directory prefers "&lt;install dir&gt;\log" — the folder the MSI
/// installer pre-creates with Everyone-write permission (see installer.wxs,
/// LogDir component). If that is not writable (a manual install into Program
/// Files without the MSI permission tweak, or a read-only deployment), it
/// falls back to %LOCALAPPDATA%\MusicPlayer\log so logging never throws.
///
/// Both the crash log (App.xaml.cs) and the lyric-completion log land here.
/// </summary>
public static class AppLog
{
    private static string? _logDir;

    /// <summary>
    /// Resolved log directory: install dir\log when writable, otherwise
    /// %LOCALAPPDATA%\MusicPlayer\log. Cached after first resolution.
    /// </summary>
    public static string LogDir
    {
        get
        {
            if (_logDir != null)
                return _logDir;

            var primary = Path.Combine(AppContext.BaseDirectory, "log");
            try
            {
                Directory.CreateDirectory(primary);
                // Probe: the directory may exist but deny writes (Program Files
                // without the installer's Everyone-write ACL).
                var probe = Path.Combine(primary, ".write-probe");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                _logDir = primary;
            }
            catch
            {
                var fallback = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "MusicPlayer", "log");
                try { Directory.CreateDirectory(fallback); } catch { /* best effort */ }
                _logDir = fallback;
            }

            return _logDir;
        }
    }

    /// <summary>
    /// Path to the lyric-completion log file (append-only across runs).
    /// Lives next to the crash log so users find all diagnostics in one place.
    /// </summary>
    public static string LyricCompletionLogPath => Path.Combine(LogDir, "LyricCompletion.log");

    /// <summary>Append a timestamped line to the lyric-completion log.</summary>
    public static void WriteLyricCompletion(string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(LyricCompletionLogPath, line, new UTF8Encoding(false));
        }
        catch
        {
            // best effort — logging must never crash the app
        }
    }

    /// <summary>Append a separator + a section header (marks a new batch run).</summary>
    public static void WriteLyricCompletionSection(string header)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.Append('=', 60).AppendLine();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {header}");
            sb.Append('=', 60).AppendLine();
            File.AppendAllText(LyricCompletionLogPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch
        {
            // best effort
        }
    }
}
