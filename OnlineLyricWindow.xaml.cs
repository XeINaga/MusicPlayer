using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Services;
using Windows.Graphics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace MusicPlayer;

/// <summary>One result row shown in the search window.</summary>
/// <param name="Song">The underlying search hit.</param>
/// <param name="Subtitle">"artist · album" line.</param>
/// <param name="Duration">Preformatted "m:ss", empty when unknown.</param>
public sealed class LyricSearchRow : System.ComponentModel.INotifyPropertyChanged
{
    public QQSong Song { get; }
    public string Subtitle { get; }
    public string Duration { get; }
    public string Title => Song.Title;

    private string _lyricState = "检测中…";
    /// <summary>Availability badge text: 检测中… / 有逐字歌词 / 无逐字歌词 / 无歌词.</summary>
    public string LyricState
    {
        get => _lyricState;
        set
        {
            if (_lyricState == value) return;
            _lyricState = value;
            PropertyChanged?.Invoke(this, new(nameof(LyricState)));
        }
    }

    private Windows.UI.Color _stateBrush = Microsoft.UI.Colors.Gray;
    /// <summary>Badge color matching <see cref="LyricState"/>.</summary>
    public Windows.UI.Color StateBrush
    {
        get => _stateBrush;
        set
        {
            if (_stateBrush == value) return;
            _stateBrush = value;
            PropertyChanged?.Invoke(this, new(nameof(StateBrush)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    public LyricSearchRow(QQSong song, string subtitle, string duration)
    {
        Song = song;
        Subtitle = subtitle;
        Duration = duration;
    }
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
            LyricSourceKind.KuGou => 3,
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
        3 => 3,
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
        // Enter in the keyword box bypasses the disabled button — a second
        // search racing the first interleaved Items mutations and let the
        // SLOWER response overwrite the newer keyword's results.
        if (!SearchBtn.IsEnabled)
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
                3 => await KugouLyricService.SearchAsync(kw),
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
        var rows = new List<LyricSearchRow>();
        foreach (var r in results)
        {
            var dur = r.DurationSec > 0 ? $"{r.DurationSec / 60}:{r.DurationSec % 60:D2}" : "";
            var subtitle = string.Join(" · ",
                new[] { r.Artist, r.Album }.Where(x => !string.IsNullOrEmpty(x)));
            var row = new LyricSearchRow(r, subtitle, dur);
            rows.Add(row);
            ResultsList.Items.Add(row);
        }

        StatusText.Text = results.Count == 0
            ? "无结果，试试只输入歌曲名"
            : $"共 {results.Count} 条结果";
        SearchBtn.IsEnabled = true;

        if (rows.Count > 0)
            StartLyricProbing(rows, SourceIndex);
    }

    // ---------- Per-row lyric availability probing ----------

    // Bumped per search so a stale probe run (older keyword) cannot overwrite
    // the badges of newer results.
    private int _probeVersion;

    /// <summary>
    /// Probe every row's lyric availability in the background (4 concurrent,
    /// one or two HTTP calls per row) and update the badges as answers come
    /// in. Never blocks the UI or the download button.
    /// </summary>
    private void StartLyricProbing(IReadOnlyList<LyricSearchRow> rows, int source)
    {
        var version = ++_probeVersion;
        var dq = DispatcherQueue;
        _ = Task.Run(async () =>
        {
            using var gate = new SemaphoreSlim(4);
            var tasks = rows.Select(r => Task.Run(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    var (text, color) = await ProbeRowAsync(r.Song, source);
                    dq.TryEnqueue(() =>
                    {
                        if (version != _probeVersion) return; // stale run
                        r.LyricState = text;
                        r.StateBrush = color;
                    });
                }
                catch
                {
                    dq.TryEnqueue(() =>
                    {
                        if (version != _probeVersion) return;
                        r.LyricState = "无歌词";
                        r.StateBrush = Microsoft.UI.Colors.Gray;
                    });
                }
                finally
                {
                    gate.Release();
                }
            })).ToList();
            await Task.WhenAll(tasks);
        });
    }

    /// <summary>One row's availability verdict: (badge text, badge color).</summary>
    private static async Task<(string Text, Windows.UI.Color Color)> ProbeRowAsync(QQSong song, int source)
    {
        var green = Windows.UI.Color.FromArgb(255, 0x31, 0xc2, 0x7c); // app accent (QqGreen)
        var grey = Microsoft.UI.Colors.Gray;

        switch (source)
        {
            case 0: // QQ: the encrypted QRC feed carries the word-timed lyric.
            {
                var raw = string.IsNullOrWhiteSpace(song.SongId)
                    ? null
                    : await QQLyricService.FetchLyricRawAsync(song.SongMid, song.SongId);
                var lyric = raw?.Lyric;
                if (!string.IsNullOrWhiteSpace(lyric))
                    // QRC word tags are INLINE after each word
                    // ("[start,dur]word(s,d)word(s,d)"), so the sniff must be
                    // the parser's own rule — a line-head-only pattern used to
                    // mark every QRC as "no word timing".
                    return LyricsParser.LooksWordTimed(lyric)
                        ? ("有逐字歌词", green)
                        : ("无逐字歌词", grey);
                return ("无歌词", grey);
            }

            case 3: // KuGou: the KRC download IS the word-timed lyric.
            {
                var (lrc, krc) = await KugouLyricService.FetchLyricAsync(song.SongMid);
                if (!string.IsNullOrWhiteSpace(krc)) return ("有逐字歌词", green);
                if (!string.IsNullOrWhiteSpace(lrc)) return ("无逐字歌词", grey);
                return ("无歌词", grey);
            }

            default: // NetEase / LRCLIB
            {
                if (source == 1)
                {
                    // NetEase serves word-timed YRC from a separate endpoint.
                    var yrc = await NetEaseLyricService.FetchYrcAsync(song.SongMid);
                    if (!string.IsNullOrWhiteSpace(yrc)) return ("有逐字歌词", green);
                }
                var lyric = source == 1
                    ? (await NetEaseLyricService.FetchLyricAsync(song.SongMid))?.Lyric
                    : (await LrclibService.FetchLyricAsync(song.SongMid))?.Lyric;
                return string.IsNullOrWhiteSpace(lyric)
                    ? ("无歌词", grey)
                    : ("无逐字歌词", grey);
            }
        }
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
            3 => "Kugou",
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
        // Only confirm when the double tap landed ON a row: double-tapping
        // empty space kept the previous selection and instantly downloaded it.
        if (e.OriginalSource is FrameworkElement fe &&
            FindAncestorListViewItem(fe) is { } container &&
            container.Content is LyricSearchRow row)
        {
            _selected = row;
            ResultsList.SelectedItem = row;
            Confirm();
        }
    }

    private static Microsoft.UI.Xaml.Controls.ListViewItem? FindAncestorListViewItem(Microsoft.UI.Xaml.DependencyObject? start)
    {
        while (start != null)
        {
            if (start is Microsoft.UI.Xaml.Controls.ListViewItem lvi)
                return lvi;
            start = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(start);
        }
        return null;
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
