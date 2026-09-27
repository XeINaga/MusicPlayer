using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Services;
using Windows.Graphics;
using System.Runtime.InteropServices;

namespace MusicPlayer;

/// <summary>One result row shown in the search window.</summary>
/// <param name="Song">The underlying search hit.</param>
/// <param name="Subtitle">"artist · album" line.</param>
/// <param name="Duration">Preformatted "m:ss", empty when unknown.</param>
public sealed record LyricSearchRow(QQSong Song, string Subtitle, string Duration)
{
    public string Title => Song.Title;
}

/// <summary>
/// Resizable online-lyric search window (replaces the old fixed-width
/// ContentDialog, whose narrow rows clipped the song duration). Styled with
/// the main window's theme tokens; the download button follows the saved
/// accent color. Size is remembered in settings.
/// </summary>
public sealed partial class OnlineLyricWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IntPtr _ownerHwnd;
    private LyricSearchRow? _selected;
    private int _frameW, _frameH; // non-client overhead of the OS frame
    private bool _suppressSourceEvents;

    /// <summary>The confirmed pick, or null when the window was cancelled.</summary>
    public (QQSong Song, int SourceIndex)? Result { get; private set; }

    /// <summary>Completed when the window closes (cancelled or confirmed).</summary>
    public Task<(QQSong Song, int SourceIndex)?> Completion { get; }

    private readonly TaskCompletionSource<(QQSong Song, int SourceIndex)?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private struct RECT { public int L, T, R, B; }
    private struct MONITORINFO { public int cbSize; public RECT Monitor, Work; public uint Flags; }

    public OnlineLyricWindow(AppSettings settings, string initialKeyword, IntPtr ownerHwnd)
    {
        _ownerHwnd = ownerHwnd;
        _settings = settings;
        Completion = _completion.Task;

        InitializeComponent();
        Root.RequestedTheme = settings.ThemeMode == "Light" ? ElementTheme.Light : ElementTheme.Dark;

        // The 下载 button reads the app accent (same source ApplyAccentColor uses).
        var accent = new SolidColorBrush(ParseAccent(settings.AccentColor));
        DownloadBtn.Background = accent;
        DownloadBtn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);

        _suppressSourceEvents = true;
        SourceCombo.SelectedIndex = LyricPreferences.ParseSource(settings.LyricSource) switch
        {
            LyricSourceKind.NetEase => 1,
            LyricSourceKind.LRCLIB => 2,
            _ => 0, // Auto and QQ both open on QQ Music
        };
        _suppressSourceEvents = false;
        KeywordBox.Text = initialKeyword;

        Title = $"搜索歌词 — {initialKeyword}";
        ConfigureWindow();
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) KeywordBox.Focus(FocusState.Programmatic); };
        Closed += (_, _) =>
        {
            // Remember the CLIENT size the user resized to. AppWindow.Size
            // includes the non-client frame (title bar), while restore goes
            // through ResizeClient — saving the raw window size here grew the
            // window by the title-bar height on every open.
            var size = AppWindow.Size;
            var cw = size.Width - _frameW;
            var ch = size.Height - _frameH;
            if (cw >= 420 && ch >= 380)
            {
                _settings.LyricSearchW = cw;
                _settings.LyricSearchH = ch;
                SettingsStore.Save(_settings);
            }
            _completion.TrySetResult(Result);
        };

        DoSearch(); // fire the initial search while the window opens
    }

    private void ConfigureWindow()
    {
        // Window.AppWindow is provided by the base class (WASDK 1.4+).

        // Icon next to the exe (copied there by the build).
        try
        {
            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (System.IO.File.Exists(icon))
                AppWindow.SetIcon(icon);
        }
        catch
        {
            // cosmetic only
        }

        var w = _settings.LyricSearchW is >= 420 and <= 4000 ? _settings.LyricSearchW : 560;
        var h = _settings.LyricSearchH is >= 380 and <= 4000 ? _settings.LyricSearchH : 640;
        AppWindow.ResizeClient(new SizeInt32(w, h));
        // Frame overhead (title bar + borders) = window size minus the client
        // area we just requested; used to convert back on save.
        _frameW = AppWindow.Size.Width - w;
        _frameH = AppWindow.Size.Height - h;
        CenterOnOwner(w + _frameW, h + _frameH);

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 420;
            p.PreferredMinimumHeight = 380;
        }

        // The default titlebar stays light regardless of the content theme —
        // paint it with the same values SukiTheme.xaml uses so the window reads
        // like the main window. (Constants mirrored from the theme dictionary;
        // AppWindow.TitleBar takes colors, not brushes.)
        var dark = Root.RequestedTheme != ElementTheme.Light;
        var tb = AppWindow.TitleBar;
        var bg = dark ? 0x0b0b11u : 0xf3f3f6u;
        var fg = dark ? 0xf2f2f5u : 0x16161cu;
        var hover = dark ? 0x23232fu : 0xe8e8efu;
        var press = dark ? 0x17171fu : 0xdcdce5u;
        tb.BackgroundColor = tb.ButtonBackgroundColor = tb.ButtonInactiveBackgroundColor = Rgb(bg);
        tb.ForegroundColor = tb.ButtonForegroundColor = tb.ButtonInactiveForegroundColor = Rgb(fg);
        tb.ButtonHoverBackgroundColor = Rgb(hover);
        tb.ButtonPressedBackgroundColor = Rgb(press);
    }

    private static Windows.UI.Color Rgb(uint v) => Windows.UI.Color.FromArgb(
        255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    /// <summary>Place the window centered on the owner (main) window, clamped
    /// to the owner's monitor work area. Falls back to the primary screen
    /// center when the owner is minimized (its rect goes far off-screen).</summary>
    private void CenterOnOwner(int windowW, int windowH)
    {
        var or = new RECT();
        var ok = _ownerHwnd != IntPtr.Zero && GetWindowRect(_ownerHwnd, ref or);
        if (!ok || or.L < -30000 || or.R - or.L <= 0)
        {
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1); // SM_CXSCREEN / CYSCREEN
            AppWindow.Move(new PointInt32((sw - windowW) / 2, (sh - windowH) / 2));
            return;
        }

        int cx = or.L + (or.R - or.L) / 2 - windowW / 2;
        int cy = or.T + (or.B - or.T) / 2 - windowH / 2;

        // Clamp into the owner's monitor work area.
        var mon = MonitorFromWindow(_ownerHwnd, 1 /* MONITOR_DEFAULTTONEAREST */);
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfoW(mon, ref mi))
        {
            cx = Math.Clamp(cx, mi.Work.L, Math.Max(mi.Work.L, mi.Work.R - windowW));
            cy = Math.Clamp(cy, mi.Work.T, Math.Max(mi.Work.T, mi.Work.B - windowH));
        }
        AppWindow.Move(new PointInt32(cx, cy));
    }

    /// <summary>Parse "#RRGGBB"/"#AARRGGBB", falling back to the default accent.</summary>
    private static Windows.UI.Color ParseAccent(string? hex)
    {
        try
        {
            var s = (hex ?? "").TrimStart('#');
            if (s.Length == 8)
                return Microsoft.UI.ColorHelper.FromArgb(
                    Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16), Convert.ToByte(s.Substring(6, 2), 16));
            if (s.Length == 6)
                return Microsoft.UI.ColorHelper.FromArgb(255,
                    Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch
        {
            // fall through to default
        }
        return Microsoft.UI.ColorHelper.FromArgb(255, 0x31, 0xc2, 0x7c);
    }

    private int SourceIndex => SourceCombo.SelectedIndex switch
    {
        1 => 1,
        2 => 2,
        _ => 0,
    };

    private async void SearchBtn_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();

    private async void KeywordBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            await DoSearchAsync();
    }

    private async void DoSearch()
    {
        try
        {
            await DoSearchAsync();
        }
        catch
        {
            // constructor-fired initial search: failures surface via DoSearchAsync's status line
        }
    }

    private async Task DoSearchAsync()
    {
        var kw = (KeywordBox.Text ?? "").Trim();
        if (kw.Length == 0)
            return;

        SearchBtn.IsEnabled = false;
        StatusText.Text = $"搜索中…（{SourceCombo.SelectedIndex switch { 1 => "网易云", 2 => "LRCLIB", _ => "QQ音乐" }}）";
        List<QQSong> results;
        try
        {
            results = SourceIndex switch
            {
                1 => await NetEaseLyricService.SearchAsync(kw),
                2 => await LrclibService.SearchAsync(kw),
                _ => await QQLyricService.SearchAsync(kw),
            };
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message; // e.g. QQ's "login required" explanation
            SearchBtn.IsEnabled = true;
            return;
        }

        ResultsList.Items.Clear();
        foreach (var r in results)
        {
            var dur = r.DurationSec > 0 ? $"{r.DurationSec / 60}:{r.DurationSec % 60:D2}" : "";
            var subtitle = string.Join(" · ",
                new[] { r.Artist, r.Album }.Where(x => !string.IsNullOrEmpty(x)));
            ResultsList.Items.Add(new LyricSearchRow(r, subtitle, dur));
        }

        StatusText.Text = results.Count == 0
            ? "无结果，试试只输入歌曲名"
            : $"共 {results.Count} 条结果";
        SearchBtn.IsEnabled = true;
    }

    private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSourceEvents)
            return;
        // Remember the pick so the dialog (and the batch auto-fill) reuse it.
        _settings.LyricSource = SourceIndex switch
        {
            1 => "NetEase",
            2 => "LRCLIB",
            _ => "QQ",
        };
        SettingsStore.Save(_settings);
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        _selected = e.ClickedItem as LyricSearchRow;
        DownloadBtn.IsEnabled = _selected != null;
    }

    private void ResultsList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // The double tap first selects the row — take the selection.
        if (ResultsList.SelectedItem is LyricSearchRow row)
        {
            _selected = row;
            Confirm();
        }
    }

    private void DownloadBtn_Click(object sender, RoutedEventArgs e) => Confirm();

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Confirm()
    {
        if (_selected == null)
            return;
        Result = (_selected.Song, SourceIndex);
        Close();
    }
}
