using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using MusicPlayer.Models;
using MusicPlayer.Services;
using Windows.Graphics;
using Windows.Storage.Streams;
using System.Runtime.InteropServices;

namespace MusicPlayer;

/// <summary>One row in the avatar-search window.</summary>
public sealed class AvatarSearchRow
{
    public CoverSearchResult Hit { get; }
    public string ArtistName => Hit.Album; // the shared result shape reuses Album as the display name
    public ImageSource? Thumb { get; }
    public string SourceTag => Hit.Extra.Length > 0 ? $"{Hit.Source} · {Hit.Extra}" : Hit.Source;

    public AvatarSearchRow(CoverSearchResult hit)
    {
        Hit = hit;
        try { Thumb = new BitmapImage(new Uri(hit.ThumbUrl)); }
        catch { Thumb = null; }
    }
}

/// <summary>
/// Manual artist-avatar search window (same layout/interaction as the album
/// cover window): search across QQ → NetEase → KuGou, preview the selected
/// picture live, then write it into the avatar cache for the target artist.
/// </summary>
public sealed partial class OnlineAvatarWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IntPtr _ownerHwnd;
    private readonly string _artistName;
    private AvatarSearchRow? _selected;
    private int _frameW, _frameH;
    private int _previewToken;
    private bool _busy;

    /// <summary>True when an avatar was picked and written; false on cancel.</summary>
    public bool Embedded { get; private set; }

    public Task<bool> Completion { get; }
    private readonly TaskCompletionSource<bool> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private struct RECT { public int L, T, R, B; }
    private struct MONITORINFO { public int cbSize; public RECT Monitor, Work; public uint Flags; }

    public OnlineAvatarWindow(AppSettings settings, string artistName, IntPtr ownerHwnd)
    {
        _ownerHwnd = ownerHwnd;
        _settings = settings;
        _artistName = artistName;
        Completion = _completion.Task;

        InitializeComponent();
        Root.RequestedTheme = settings.ThemeMode == "Light" ? ElementTheme.Light : ElementTheme.Dark;

        var accent = AccentColor(settings.AccentColor);
        EmbedBtn.Background = new SolidColorBrush(accent);
        EmbedBtn.Foreground = new SolidColorBrush(Microsoft.UI.Colors.White);

        KeywordBox.Text = artistName;
        Title = $"搜索歌手头像 — {artistName}";
        ConfigureWindow();
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) KeywordBox.Focus(FocusState.Programmatic); };
        Closed += (_, _) => _completion.TrySetResult(Embedded);

        DoSearch(); // initial search with the artist's name
    }

    private static Windows.UI.Color AccentColor(string? hex)
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
        catch { }
        return Microsoft.UI.ColorHelper.FromArgb(255, 0x31, 0xc2, 0x7c);
    }

    private void ConfigureWindow()
    {
        try
        {
            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (System.IO.File.Exists(icon))
                AppWindow.SetIcon(icon);
        }
        catch { }

        // Shares the cover-search window size — same shape of window.
        var w = _settings.CoverSearchW is >= 560 and <= 4000 ? _settings.CoverSearchW : 780;
        var h = _settings.CoverSearchH is >= 480 and <= 4000 ? _settings.CoverSearchH : 600;
        AppWindow.ResizeClient(new SizeInt32(w, h));
        _frameW = AppWindow.Size.Width - w;
        _frameH = AppWindow.Size.Height - h;
        CenterOnOwner(w + _frameW, h + _frameH);

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 560;
            p.PreferredMinimumHeight = 480;
        }

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

    private void CenterOnOwner(int windowW, int windowH)
    {
        var or = new RECT();
        var ok = _ownerHwnd != IntPtr.Zero && GetWindowRect(_ownerHwnd, ref or);
        if (!ok || or.L < -30000 || or.R - or.L <= 0)
        {
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            AppWindow.Move(new PointInt32((sw - windowW) / 2, (sh - windowH) / 2));
            return;
        }
        int cx = or.L + (or.R - or.L) / 2 - windowW / 2;
        int cy = or.T + (or.B - or.T) / 2 - windowH / 2;
        var mon = MonitorFromWindow(_ownerHwnd, 1);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfoW(mon, ref mi))
        {
            cx = Math.Clamp(cx, mi.Work.L, Math.Max(mi.Work.L, mi.Work.R - windowW));
            cy = Math.Clamp(cy, mi.Work.T, Math.Max(mi.Work.T, mi.Work.B - windowH));
        }
        AppWindow.Move(new PointInt32(cx, cy));
    }

    private int SourceIndex => SourceCombo.SelectedIndex switch
    {
        1 => ArtistAvatarService.SourceQq,
        2 => ArtistAvatarService.SourceNetEase,
        3 => ArtistAvatarService.SourceKugou,
        _ => ArtistAvatarService.SourceAuto,
    };

    private async void SearchBtn_Click(object sender, RoutedEventArgs e) => await DoSearchAsync();

    private async void KeywordBox_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            await DoSearchAsync();
    }

    private async void DoSearch()
    {
        try { await DoSearchAsync(); }
        catch { /* initial search failures surface via StatusText */ }
    }

    private async Task DoSearchAsync()
    {
        var kw = (KeywordBox.Text ?? "").Trim();
        if (kw.Length == 0 || !SearchBtn.IsEnabled)
            return;

        SearchBtn.IsEnabled = false;
        StatusText.Text = "搜索中…";
        System.Collections.Generic.List<CoverSearchResult> results;
        try
        {
            results = await ArtistAvatarService.SearchAsync(kw, SourceIndex);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"搜索失败：{ex.Message}";
            SearchBtn.IsEnabled = true;
            return;
        }

        _selected = null;
        EmbedBtn.IsEnabled = false;
        PreviewImage.Source = null;
        PreviewInfo.Text = "选择结果后在此预览";
        ResultsList.ItemsSource = results.Select(r => new AvatarSearchRow(r)).ToList();

        StatusText.Text = results.Count == 0
            ? "无结果，试试换关键词或来源"
            : $"共 {results.Count} 个结果";
        SearchBtn.IsEnabled = true;
    }

    private async void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selected = ResultsList.SelectedItem as AvatarSearchRow;
        EmbedBtn.IsEnabled = _selected != null;
        await ShowPreviewAsync(_selected);
    }

    /// <summary>Download the selected hit's big picture and show it. Token-guarded
    /// so a slow older download can't paint over the newer selection.</summary>
    private async Task ShowPreviewAsync(AvatarSearchRow? row)
    {
        var token = ++_previewToken;
        if (row == null)
        {
            PreviewImage.Source = null;
            PreviewInfo.Text = "选择结果后在此预览";
            return;
        }

        PreviewInfo.Text = "加载预览…";
        PreviewImage.Source = null;
        try
        {
            var dl = await CoverArtService.DownloadAsync(row.Hit.PicUrl);
            if (token != _previewToken) return;
            if (dl == null)
            {
                PreviewInfo.Text = "预览加载失败";
                return;
            }

            var bmp = new BitmapImage();
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(dl.Value.Data);
            await writer.StoreAsync();
            writer.FlushAsync().AsTask().Wait();
            stream.Seek(0);
            await bmp.SetSourceAsync(stream);
            if (token != _previewToken) return;

            PreviewImage.Source = bmp;
            PreviewInfo.Text = $"{row.ArtistName}\n{dl.Value.Data.Length / 1024} KB · {row.Hit.Source}";
        }
        catch
        {
            if (token == _previewToken)
                PreviewInfo.Text = "预览加载失败";
        }
    }

    private async void EmbedBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_selected == null || _busy)
            return;
        _busy = true;
        EmbedBtn.IsEnabled = false;
        StatusText.Text = "下载图片并写入…";

        try
        {
            var dl = await CoverArtService.DownloadAsync(_selected.Hit.PicUrl);
            if (dl == null)
            {
                StatusText.Text = "图片下载失败，请重试或换一个结果";
                return;
            }

            ArtistAvatarService.SaveAvatar(_artistName, dl.Value.Data, dl.Value.Mime);
            Embedded = true;
            StatusText.Text = "已写入头像";
            Close();
        }
        finally
        {
            _busy = false;
            EmbedBtn.IsEnabled = _selected != null && !Embedded;
        }
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();
}
