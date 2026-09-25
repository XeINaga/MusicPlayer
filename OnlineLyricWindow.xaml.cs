using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Services;
using Windows.Graphics;

namespace MusicPlayer;

/// <summary>
/// Resizable online-lyric search window (replaces the old fixed-width
/// ContentDialog, whose narrow rows clipped the song duration). Size is
/// remembered in settings; rows keep duration in its own right-aligned
/// column so it stays visible at any width.
/// </summary>
public sealed partial class OnlineLyricWindow : Window
{
    private static readonly (string Name, int Index)[] Sources =
        { ("QQ音乐", 0), ("网易云", 1), ("LRCLIB", 2) };

    private readonly AppSettings _settings;
    private QQSong? _selected;
    private bool _suppressSourceEvents;

    /// <summary>The confirmed pick, or null when the window was cancelled.</summary>
    public (QQSong Song, int SourceIndex)? Result { get; private set; }

    /// <summary>Completed when the window closes (cancelled or confirmed).</summary>
    public Task<(QQSong Song, int SourceIndex)?> Completion { get; }

    private readonly TaskCompletionSource<(QQSong Song, int SourceIndex)?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public OnlineLyricWindow(AppSettings settings, string initialKeyword)
    {
        _settings = settings;
        Completion = _completion.Task;

        InitializeComponent();
        Root.RequestedTheme = settings.ThemeMode == "Light" ? ElementTheme.Light : ElementTheme.Dark;

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
            // Remember the size the user resized to.
            var size = AppWindow.Size;
            if (size.Width >= 420 && size.Height >= 380)
            {
                _settings.LyricSearchW = size.Width;
                _settings.LyricSearchH = size.Height;
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

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 420;
            p.PreferredMinimumHeight = 380;
        }
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
        StatusText.Text = $"搜索中…（{Sources[SourceIndex].Name}）";
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
            var item = new Grid
            {
                Tag = r,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 3, 0, 3),
                ColumnSpacing = 10,
            };
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var texts = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock
            {
                Text = r.Title,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextPrimary"],
            });
            texts.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", new[] { r.Artist, r.Album }.Where(x => !string.IsNullOrEmpty(x))),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)Application.Current.Resources["TextSecondary"],
            });
            Grid.SetColumn(texts, 0);
            item.Children.Add(texts);

            var duration = new TextBlock
            {
                Text = dur,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 34,
                TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right,
                Foreground = (Brush)Application.Current.Resources["TextSecondary"],
            };
            Grid.SetColumn(duration, 1);
            item.Children.Add(duration);

            ResultsList.Items.Add(item);
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
        _selected = (e.ClickedItem as FrameworkElement)?.Tag as QQSong;
        DownloadBtn.IsEnabled = _selected != null;
    }

    private void ResultsList_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        // Resolve the row under the cursor (DoubleClick works on any part of it).
        if (e.OriginalSource is DependencyObject d &&
            FindAncestor<Grid>(d) is Grid { Tag: QQSong song })
        {
            _selected = song;
            Confirm();
        }
    }

    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        var d = start;
        while (d != null && d is not T)
            d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d);
        return d as T;
    }

    private void DownloadBtn_Click(object sender, RoutedEventArgs e) => Confirm();

    private void CancelBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void Confirm()
    {
        if (_selected == null)
            return;
        Result = (_selected, SourceIndex);
        Close();
    }
}
