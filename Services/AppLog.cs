using System;
using System.IO;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>
/// Central log-directory resolution + lightweight append-only log writers.
///
/// The log directory is always "&lt;install dir&gt;\log" — the folder the MSI
/// installer pre-creates (see installer.wxs, LogDir component).
///
/// Both the crash log (App.xaml.cs) and the lyric-completion log land here.
/// </summary>
public static class AppLog
{
    private static string? _logDir;

    /// <summary>
    /// Resolved log directory under the application installation directory.
    /// </summary>
    public static string LogDir
    {
        get
        {
            if (_logDir != null)
                return _logDir;

            var primary = Path.Combine(AppContext.BaseDirectory, "log");
            try { Directory.CreateDirectory(primary); } catch { /* best effort */ }
            _logDir = primary;

            return _logDir;
        }
    }

    /// <summary>
    /// Path to the lyric-completion log file (append-only across runs).
    /// Lives next to the crash log so users find all diagnostics in one place.
    /// </summary>
    public static string LyricCompletionLogPath => Path.Combine(LogDir, "LyricCompletion.log");

    /// <summary>Append a timestamped line to the lyric-completion log.
    /// Rotates the file to ".1" once it exceeds 5 MB so long-term use cannot
    /// grow a single log without bound.</summary>
    public static void WriteLyricCompletion(string message)
    {
        try
        {
            RotateIfNeeded(LyricCompletionLogPath);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            File.AppendAllText(LyricCompletionLogPath, line, new UTF8Encoding(false));
        }
        catch
        {
            // best effort — logging must never crash the app
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 5 * 1024 * 1024)
            {
                var backup = path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
            }
        }
        catch
        {
            // rotation is best effort; append still works
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

    public static void WritePersistenceFailure(string path, Exception exception)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Persistence failure: {path}{Environment.NewLine}" +
                       $"{exception}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(LogDir, "MusicPlayer.log"), line, new UTF8Encoding(false));
        }
        catch
        {
        }
    }
}
