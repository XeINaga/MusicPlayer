using Microsoft.UI.Xaml;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MusicPlayer;

/// <summary>
/// Provides application-specific behavior.
/// </summary>
public partial class App : Application
{
    /// <summary>Reference to the main window (used by child windows if needed).</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        // Register legacy codepage provider so LRC files saved in GBK / Shift-JIS
        // (e.g. QQ Music downloads) decode correctly instead of showing mojibake.
        // Without this, Encoding.GetEncoding("GBK") throws and falls back to UTF-8.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Catch managed exceptions so the app does not silently vanish on startup.
        this.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandled;

        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            LogCrash("OnLaunched", ex);
            ShowCrash(ex);
            throw; // rethrow so the process exits cleanly after the message box
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogCrash("UnhandledException", e.Exception);
        ShowCrash(e.Exception);
    }

    private void OnAppDomainUnhandled(object? sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            LogCrash("AppDomainUnhandled", ex);
    }

    private static string CrashLogPath =>
        Path.Combine(Path.GetTempPath(), "MusicPlayer_crash.log");

    private static void LogCrash(string where, Exception ex)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}:");
            AppendException(sb, ex, 0);
            sb.AppendLine();
            File.AppendAllText(CrashLogPath, sb.ToString());
        }
        catch
        {
            // best effort only
        }
    }

    /// <summary>
    /// WinUI 3 hides the REAL cause of a XamlParseException in
    /// ex.Data["RestrictedDescription"] (e.g. "Cannot create instance of type 'X' [Line: Y Position: Z]").
    /// Surface it so the failure is diagnosable instead of a silent "XAML parsing failed."
    /// </summary>
    private static string? RestrictedDescription(Exception ex) =>
        ex.Data?["RestrictedDescription"] as string;

    private static void AppendException(System.Text.StringBuilder sb, Exception? ex, int depth)
    {
        if (ex == null)
            return;
        var indent = new string(' ', depth * 2);
        sb.AppendLine($"{indent}- {ex.GetType().FullName}");
        sb.AppendLine($"{indent}  HResult: 0x{ex.HResult:X8}");
        sb.AppendLine($"{indent}  Message: {ex.Message}");

        var restricted = RestrictedDescription(ex);
        if (!string.IsNullOrEmpty(restricted))
            sb.AppendLine($"{indent}  RestrictedDescription: {restricted}");

        if (ex.Data != null && ex.Data.Count > 0)
        {
            sb.AppendLine($"{indent}  Data:");
            foreach (System.Collections.DictionaryEntry kv in ex.Data)
                sb.AppendLine($"{indent}    [{kv.Key}] = {kv.Value}");
        }

        sb.AppendLine($"{indent}  Stack:");
        sb.AppendLine($"{indent}    {ex.StackTrace?.Replace("\n", "\n" + indent + "    ")}");
        if (ex.InnerException != null)
        {
            sb.AppendLine($"{indent}  InnerException:");
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }

    private static void ShowCrash(Exception ex)
    {
        try
        {
            var restricted = RestrictedDescription(ex);
            var detail = !string.IsNullOrEmpty(restricted)
                ? restricted
                : $"{ex.GetType().Name}: {ex.Message}";
            var msg = "MusicPlayer 启动失败：\r\n" +
                      $"{detail}\r\n\r\n" +
                      $"详细错误已写入：\r\n{CrashLogPath}";
            MessageBoxW(IntPtr.Zero, msg, "MusicPlayer 错误", 0x10 /* MB_ICONERROR */);
        }
        catch
        {
            // best effort only
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
