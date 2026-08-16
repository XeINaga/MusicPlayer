using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Windows.ApplicationModel.DataTransfer;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;
using MusicPlayer.Models;
using MusicPlayer.Services;

namespace MusicPlayer;

public sealed partial class MainWindow : Window
{
    private enum NavView { Local, Recent, Playlist, Settings, NowPlaying }

    private readonly PlaybackService _playback = new();
    private readonly ObservableCollection<Track> _library = new();
    private readonly ObservableCollection<Track> _recent = new();
    private readonly ObservableCollection<Playlist> _playlists = new();
    private readonly AppSettings _settings = SettingsStore.Load();
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<Track> _displayTracks = new();

    private IList<Track> _activeTracks = new ObservableCollection<Track>();
    private NavView _currentView = NavView.Local;
    private Playlist? _currentPlaylist;
    private IList<Track>? _boundQueue;

    private LyricDocument? _lyrics;
    private readonly List<StackPanel> _lyricPanels = new();
    private int _currentLineIndex = -1;
    private int _loadedIndex = -1;

    private DesktopLyricsOverlay? _desktopLyrics;
    private readonly PlayMode[] _modeOrder = { PlayMode.Sequential, PlayMode.LoopAll, PlayMode.LoopOne, PlayMode.Random };

    private bool _isSeeking;
    private bool _sized;
    private bool _isPlaying;
    private DateTime _lastProgressSave = DateTime.MinValue;
    private Track? _currentTrack;
    private Track? _contextTrack;
    private string _searchText = string.Empty;

    private string _viewMode = "Grid";   // "Grid" | "List"
    private string _sortBy = "Default";   // Default|Title|Artist|Album|DateAdded|Duration

    // Close-to-tray support.
    private TrayIconService? _tray;
    private bool _forceExit;         // real exit requested (tray menu / second close)
    private bool _trayHintShown;     // balloon only on the first hide per session

    // "Recently played" bookkeeping: the restored session queue must not count
    // as "played" until playback actually starts.
    private int _lastRecentIndex = -1;
    private bool _suppressNextRecent;
    // Consecutive playback failures (stop skipping when the whole queue is bad).
    private int _consecutiveFailures;
    private readonly DispatcherTimer _errorBarTimer = new();

    // Multi-select mode for batch "add to playlist".
    private bool _selectMode;
    // Current track captured when a queue drag starts (to resync the index).
    private Track? _queueDragCurrent;

    private readonly DispatcherTimer _discTimer = new();
    private readonly RotateTransform _discRotate = new();
    private static readonly Brush CoverPlaceholder =
        new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x15, 0x15, 0x1c));

    public MainWindow()
    {
        this.InitializeComponent();

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        PlaylistsList.ItemsSource = _playlists;
        TrackGrid.ItemsSource = _displayTracks;
        TrackList.ItemsSource = _displayTracks;

        _playback.PositionTick += OnPositionTick;
        _playback.StateChanged += OnStateChanged;
        _playback.CurrentIndexChanged += OnCurrentIndexChanged;
        _playback.MediaFailed += OnPlaybackMediaFailed;
        // SMTC shuffle/repeat buttons can change the mode outside the UI.
        _playback.ModeChanged += () => ApplyPlayModeLabel();

        _errorBarTimer.Interval = TimeSpan.FromSeconds(5);
        _errorBarTimer.Tick += (_, _) =>
        {
            _errorBarTimer.Stop();
            PlayErrorBar.IsOpen = false;
        };

        // Restore persisted volume (so it matches the last session).
        _playback.Volume = _settings.Volume;
        VolumeSlider.Value = _settings.Volume * 100.0;
        SeekSlider.Maximum = 1;

        // Apply the persisted theme color before the first paint.
        ApplyAccentColor();

        // Mica window backdrop (Windows 11+): the desktop material tints the
        // whole window. The layered fills in XAML are semi-transparent for
        // exactly this case; on unsupported systems the opaque RootGrid
        // fallback color stays and the layout looks the same as before.
        if (Microsoft.UI.Composition.SystemBackdrops.MicaController.IsSupported())
        {
            SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
            RootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }

        // Apply the persisted data/cache directory (default = %LOCALAPPDATA%\MusicPlayer).
        // Must run before any PlaylistStore / LyricBindingStore access below.
        DataLocation.Apply(_settings.CacheDir);

        // Restore view + sort preferences.
        _viewMode = _settings.ViewMode == "List" ? "List" : "Grid";
        _sortBy = _settings.SortBy;

        // Spinning vinyl disc (animates only while playing AND spin is enabled).
        CoverDisc.RenderTransform = _discRotate;
        CoverDisc.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _discTimer.Interval = TimeSpan.FromMilliseconds(40);
        _discTimer.Tick += (_, _) =>
        {
            if (_isPlaying && _settings.CoverSpin)
                _discRotate.Angle = (_discRotate.Angle + 0.9) % 360;
        };
        _discTimer.Start();

        // Restore persisted play mode.
        if (Enum.TryParse<PlayMode>(_settings.DefaultPlayMode, out var m))
            _playback.Mode = m;
        ApplyPlayModeLabel();

        UpdateLyricOffsetText();
        LyricRomajiToggle.IsChecked = _settings.LyricShowRomaji;
        LyricTransToggle.IsChecked = _settings.LyricShowTranslation;
        WireSeekSlider();

        // Slider range set in code: this exact slider's Minimum/Maximum as XAML
        // attributes produced a corrupt XBF node ("Failed to assign to property
        // 'RangeBase.Minimum'" at runtime) even though it compiled fine.
        LyricFontSlider.Minimum = 12;
        LyricFontSlider.Maximum = 72;

        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;

        RestoreSession();
        ShowView(NavView.Local);

        // Apply the embedded app icon to the window title bar / taskbar.
        TrySetWindowIcon();

        // Modern merged title bar: XAML content extends into the caption area.
        SetupTitleBar();
    }

    /// <summary>
    /// Extends the window content into the title bar (the AppTitleBar strip in
    /// XAML becomes the drag region) and re-colors the system caption buttons
    /// so they blend with the dark theme instead of the default white bar.
    /// </summary>
    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var titleBar = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId).TitleBar;

            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonForegroundColor = ParseHex("#f2f2f5");
            titleBar.ButtonHoverBackgroundColor = ParseHex("#23232f");
            titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonPressedBackgroundColor = ParseHex("#2c2c3a");
            titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
            titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
            titleBar.ButtonInactiveForegroundColor = ParseHex("#6a6a76");
        }
        catch
        {
            // best-effort: without this the default caption colors remain.
        }
    }

    // ---------- Tray (close-to-tray) ----------

    private void EnsureTrayIcon()
    {
        if (_tray != null)
            return;

        var iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
        _tray = new TrayIconService();
        _tray.OpenRequested += () => _dispatcher.TryEnqueue(RestoreFromTray);
        _tray.ExitRequested += () => _dispatcher.TryEnqueue(() =>
        {
            _forceExit = true;
            Close();
        });
        _tray.Show(iconPath, "MusicPlayer — 音乐播放器");

        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _tray.ShowBalloon("MusicPlayer", "已最小化到托盘，点击托盘图标可恢复窗口。");
        }
    }

    private void RestoreFromTray()
    {
        try
        {
            this.AppWindow.Show();
            this.Activate();
        }
        catch
        {
            // best-effort
        }
    }

    private void TrySetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (!File.Exists(iconPath))
                return;
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
            appWindow.SetIcon(iconPath);
        }
        catch
        {
            // best-effort; the exe's embedded icon still covers the taskbar.
        }
    }

    // ---------- Session restore (no auto-play) ----------

    private void RestoreSession()
    {
        var paths = PlaylistStore.LoadAutoPlaylist();
        if (paths.Count > 0)
            AddItemsToLibrary(paths, persist: false);

        LoadRecentFromStore();
        LoadPlaylistsFromStore();

        var prog = PlaylistStore.LoadProgress();
        if (!string.IsNullOrEmpty(prog.Path))
        {
            var idx = _library.ToList().FindIndex(t => t.Path == prog.Path);
            if (idx >= 0)
            {
                _activeTracks = _library;
                // Load the queue but DO NOT start playing — the user resumes manually.
                // Also don't count this as "recently played" until playback starts.
                _suppressNextRecent = true;
                _playback.SetQueue(_library, idx, TimeSpan.FromMilliseconds(prog.PositionMs), autoPlay: false);
                BindQueue();
            }
        }
    }

    // ---------- Navigation ----------

    private void NavRecent_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Recent);
    private void NavLocal_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Local);
    private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Settings);

    private void ShowView(NavView view, Playlist? playlist = null)
    {
        _currentView = view;
        _currentPlaylist = playlist;

        if (SearchBox != null)
        {
            SearchBox.Text = string.Empty;
            _searchText = string.Empty;
        }

        switch (view)
        {
            case NavView.Local:
                ContentTitle.Text = "本地音乐";
                _activeTracks = _library;
                ShowActions("local");
                break;
            case NavView.Recent:
                ContentTitle.Text = "最近播放";
                _activeTracks = _recent;
                ShowActions("recent");
                break;
            case NavView.Playlist:
                ContentTitle.Text = playlist?.Name ?? "歌单";
                _activeTracks = playlist?.Tracks ?? _library;
                ShowActions("playlist");
                break;
            case NavView.Settings:
                ContentTitle.Text = "设置";
                ShowActions("settings");
                ShowSettings();
                break;
            case NavView.NowPlaying:
                ContentTitle.Text = "正在播放";
                break;
        }

        ApplyViewMode();
        ApplySortSelection();
        BtnSelectMode.IsChecked = false; // reset multi-select on view switch
        RefreshDisplay();
        UpdateViewVisibility();
        SetNavSelected(view);
    }

    /// <summary>
    /// Drives which regions of the window body are visible for the current view.
    /// Library views show the center list + search/sort header; Settings shows the
    /// settings panel across the center; NowPlaying takes over the whole body
    /// (collapses the center column and expands the right panel) so clicking the
    /// mini-cover switches the entire window instead of only popping a side panel.
    /// </summary>
    private void UpdateViewVisibility()
    {
        bool library = _currentView is NavView.Local or NavView.Recent or NavView.Playlist;
        bool grid = _viewMode == "Grid";

        LibraryHeader.Visibility = library ? Visibility.Visible : Visibility.Collapsed;

        if (_currentView == NavView.NowPlaying)
        {
            CenterCol.Width = new GridLength(0);
            RightCol.Width = new GridLength(1, GridUnitType.Star);
            CenterGrid.Visibility = Visibility.Collapsed;
            NowPlayingPanel.Visibility = Visibility.Visible;
            TrackGrid.Visibility = Visibility.Collapsed;
            TrackList.Visibility = Visibility.Collapsed;
            SettingsScroll.Visibility = Visibility.Collapsed;
        }
        else
        {
            CenterCol.Width = new GridLength(1, GridUnitType.Star);
            RightCol.Width = new GridLength(0);
            CenterGrid.Visibility = Visibility.Visible;
            NowPlayingPanel.Visibility = Visibility.Collapsed;
            SettingsScroll.Visibility = _currentView == NavView.Settings ? Visibility.Visible : Visibility.Collapsed;

            if (library)
            {
                TrackGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
                TrackList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
            }
            else
            {
                TrackGrid.Visibility = Visibility.Collapsed;
                TrackList.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void ShowActions(string which)
    {
        BtnAddFile.Visibility = Visibility.Collapsed;
        BtnAddFolderRecursive.Visibility = Visibility.Collapsed;
        BtnAddFolderFlat.Visibility = Visibility.Collapsed;
        BtnOpenList.Visibility = Visibility.Collapsed;
        BtnSaveList.Visibility = Visibility.Collapsed;
        BtnClear.Visibility = Visibility.Collapsed;
        BtnClearRecent.Visibility = Visibility.Collapsed;
        BtnPlayPlaylist.Visibility = Visibility.Collapsed;
        BtnAddToPlaylist.Visibility = Visibility.Collapsed;
        BtnDeletePlaylist.Visibility = Visibility.Collapsed;
        BtnRenamePlaylist.Visibility = Visibility.Collapsed;
        BtnSelectMode.Visibility = Visibility.Collapsed;
        BtnBatchLyrics.Visibility = Visibility.Collapsed;
        ViewCombo.Visibility = Visibility.Collapsed;
        SortCombo.Visibility = Visibility.Collapsed;

        switch (which)
        {
            case "local":
                BtnAddFile.Visibility = Visibility.Visible;
                BtnAddFolderRecursive.Visibility = Visibility.Visible;
                BtnAddFolderFlat.Visibility = Visibility.Visible;
                BtnOpenList.Visibility = Visibility.Visible;
                BtnSaveList.Visibility = Visibility.Visible;
                BtnClear.Visibility = Visibility.Visible;
                BtnSelectMode.Visibility = Visibility.Visible;
                BtnBatchLyrics.Visibility = Visibility.Visible;
                ViewCombo.Visibility = Visibility.Visible;
                SortCombo.Visibility = Visibility.Visible;
                break;
            case "recent":
                BtnClearRecent.Visibility = Visibility.Visible;
                BtnSelectMode.Visibility = Visibility.Visible;
                ViewCombo.Visibility = Visibility.Visible;
                SortCombo.Visibility = Visibility.Visible;
                break;
            case "playlist":
                BtnPlayPlaylist.Visibility = Visibility.Visible;
                BtnAddToPlaylist.Visibility = Visibility.Visible;
                BtnDeletePlaylist.Visibility = Visibility.Visible;
                BtnRenamePlaylist.Visibility = Visibility.Visible;
                BtnSelectMode.Visibility = Visibility.Visible;
                ViewCombo.Visibility = Visibility.Visible;
                SortCombo.Visibility = Visibility.Visible;
                break;
        }
    }

    private void SetNavSelected(NavView view)
    {
        NavRecent.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        NavLocal.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        NavSettings.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var sel = view switch
        {
            NavView.Recent => NavRecent,
            NavView.Settings => NavSettings,
            NavView.NowPlaying => null,
            _ => NavLocal
        };
        if (sel != null)
            sel.Background = (SolidColorBrush)RootGrid.Resources["NavSelected"];
    }

    // ---------- View mode + sorting ----------

    private void ApplyViewMode()
    {
        bool grid = _viewMode == "Grid";
        TrackGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        TrackList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
        ViewCombo.SelectedIndex = grid ? 0 : 1;
    }

    private void ApplySortSelection()
    {
        SortCombo.SelectedIndex = _sortBy switch
        {
            "Title" => 1,
            "Artist" => 2,
            "Album" => 3,
            "DateAdded" => 4,
            "Duration" => 5,
            _ => 0
        };
    }

    private void ViewCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _viewMode = ViewCombo.SelectedIndex == 1 ? "List" : "Grid";
        _settings.ViewMode = _viewMode;
        SettingsStore.Save(_settings);
        ApplyViewMode();
    }

    private void SortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sortBy = SortCombo.SelectedIndex switch
        {
            1 => "Title",
            2 => "Artist",
            3 => "Album",
            4 => "DateAdded",
            5 => "Duration",
            _ => "Default"
        };
        _settings.SortBy = _sortBy;
        SettingsStore.Save(_settings);
        RefreshDisplay();
    }

    // ---------- Adding music (lives in 本地音乐) ----------

    private async void BtnAddFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitPicker(picker);
        picker.ViewMode = PickerViewMode.List;
        foreach (var ext in FolderScanner.AudioExtensions)
            picker.FileTypeFilter.Add(ext);

        var files = await picker.PickMultipleFilesAsync();
        if (files == null)
            return;

        var paths = files.Select(f => f.Path).Where(FolderScanner.IsAudio).ToList();
        if (paths.Count > 0)
            AddItemsToLibrary(paths);
    }

    private async void BtnAddFolderRecursive_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder == null)
            return;
        AddItemsToLibrary(FolderScanner.Scan(folder.Path, recursive: true));
    }

    private async void BtnAddFolderFlat_Click(object sender, RoutedEventArgs e)
    {
        var folder = await PickFolderAsync();
        if (folder == null)
            return;
        AddItemsToLibrary(FolderScanner.Scan(folder.Path, recursive: false));
    }

    private async void BtnOpenList_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitPicker(picker);
        picker.FileTypeFilter.Add(".m3u");
        picker.FileTypeFilter.Add(".m3u8");
        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;
        AddItemsToLibrary(PlaylistStore.ImportM3U(file.Path));
    }

    private async void BtnSaveList_Click(object sender, RoutedEventArgs e)
    {
        if (_library.Count == 0)
            return;
        var picker = new FileSavePicker();
        InitPicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        picker.FileTypeChoices.Add("播放列表", new[] { ".m3u" });
        picker.SuggestedFileName = "我的播放列表";
        var file = await picker.PickSaveFileAsync();
        if (file != null)
            PlaylistStore.ExportM3U(file.Path, _library.ToList());
    }

    private void AddItemsToLibrary(List<string> paths, bool persist = true)
    {
        if (paths.Count == 0)
            return;

        var added = false;
        foreach (var p in paths)
        {
            if (_library.Any(t => t.Path == p))
                continue;
            var track = new Track(p);
            track.LyricPath = LyricBindingStore.Get(p);
            _library.Add(track);
            LoadMetadataFor(track);
            added = true;
        }

        if (persist)
            PersistLibrary();

        if (added && _playback.Queue == null && _library.Count > 0)
        {
            _activeTracks = _library;
            LoadLyricsFor(0);
        }

        RefreshDisplay();
    }

    private void LoadMetadataFor(Track track) =>
        _ = MetadataService.LoadAsync(track, _dispatcher);

    private void PersistLibrary() =>
        PlaylistStore.SaveAutoPlaylist(_library.Select(t => t.Path).ToList());

    private void PersistRecent() =>
        PlaylistStore.SaveRecent(_recent.Select(t => t.Path).ToList());

    private void PersistPlaylists() =>
        PlaylistStore.SavePlaylists(_playlists.Select(p => new PlaylistDto
        {
            Name = p.Name,
            Paths = p.Tracks.Select(t => t.Path).ToList()
        }).ToList());

    /// <summary>Find a track by path, creating + registering it in the library if missing.</summary>
    private Track? ResolveTrack(string path)
    {
        var existing = _library.FirstOrDefault(t => t.Path == path);
        if (existing != null)
            return existing;

        var track = new Track(path);
        track.LyricPath = LyricBindingStore.Get(path);
        _library.Add(track);
        LoadMetadataFor(track);
        return track;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _library.Clear();
        _playback.Clear();
        _boundQueue = null;
        QueueList.ItemsSource = null;
        ResetNowPlaying();
        PersistLibrary();
        RefreshDisplay();
    }

    private void BtnClearRecent_Click(object sender, RoutedEventArgs e)
    {
        _recent.Clear();
        PersistRecent();
        RefreshDisplay();
    }

    // ---------- Recent / playlists persistence ----------

    private void LoadRecentFromStore()
    {
        foreach (var p in PlaylistStore.LoadRecent())
        {
            var t = ResolveTrack(p);
            if (t != null)
                _recent.Add(t);
        }
    }

    private void LoadPlaylistsFromStore()
    {
        foreach (var dto in PlaylistStore.LoadPlaylists())
        {
            var pl = new Playlist { Name = dto.Name };
            if (dto.Paths != null)
            {
                foreach (var p in dto.Paths)
                {
                    var t = ResolveTrack(p);
                    if (t != null)
                        pl.Tracks.Add(t);
                }
            }
            _playlists.Add(pl);
        }
    }

    private void PushRecent(Track track)
    {
        _recent.Remove(track);
        _recent.Insert(0, track);
        while (_recent.Count > 200)
            _recent.RemoveAt(_recent.Count - 1);
        PersistRecent();
    }

    // ---------- Playlists UI ----------

    private async void BtnNewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var nameBox = new TextBox { PlaceholderText = "请输入歌单名称", Width = 280 };
        var hint = new TextBlock { Text = " ", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 12 };
        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "新建歌单",
            PrimaryButtonText = "创建",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "歌单名称", Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray), FontSize = 12 },
                    nameBox,
                    hint
                }
            }
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var name = (nameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                // Keep the dialog open so the user can actually type a name.
                args.Cancel = true;
                hint.Text = "歌单名称不能为空";
                return;
            }

            var pl = new Playlist { Name = name };
            _playlists.Add(pl);
            PersistPlaylists();
            ShowView(NavView.Playlist, pl);
        };

        await dialog.ShowAsync();
    }

    private void PlaylistsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Playlist pl)
            ShowView(NavView.Playlist, pl);
    }

    private void BtnPlayPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist == null || _currentPlaylist.Tracks.Count == 0)
            return;
        StartPlay(_currentPlaylist.Tracks, 0);
    }

    private async void BtnAddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist == null)
            return;

        var picker = new FileOpenPicker();
        InitPicker(picker);
        picker.ViewMode = PickerViewMode.List;
        foreach (var ext in FolderScanner.AudioExtensions)
            picker.FileTypeFilter.Add(ext);

        var files = await picker.PickMultipleFilesAsync();
        if (files == null)
            return;

        foreach (var f in files.Select(x => x.Path).Where(FolderScanner.IsAudio))
        {
            var t = ResolveTrack(f);
            if (t != null && !_currentPlaylist.Tracks.Contains(t))
                _currentPlaylist.Tracks.Add(t);
        }

        PersistPlaylists();
        PersistLibrary();
        RefreshDisplay();
    }

    private async void BtnDeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist == null)
            return;
        await DeletePlaylistConfirmed(_currentPlaylist);
    }

    private async void BtnRenamePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist == null)
            return;
        await RenamePlaylistDialog(_currentPlaylist);
    }

    private void PlaylistItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Playlist pl)
            return;
        e.Handled = true;

        var flyout = new MenuFlyout();
        var rename = new MenuFlyoutItem { Text = "重命名歌单", Icon = new FontIcon { Glyph = "\uE8AC" } };
        rename.Click += async (_, _) => await RenamePlaylistDialog(pl);
        flyout.Items.Add(rename);

        var del = new MenuFlyoutItem { Text = "删除歌单", Icon = new FontIcon { Glyph = "\uE74D" } };
        del.Click += async (_, _) => await DeletePlaylistConfirmed(pl);
        flyout.Items.Add(del);

        flyout.ShowAt(fe, e.GetPosition(fe));
    }

    private async Task RenamePlaylistDialog(Playlist pl)
    {
        var nameBox = new TextBox { Text = pl.Name, Width = 280 };
        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "重命名歌单",
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = nameBox
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        var name = (nameBox.Text ?? string.Empty).Trim();
        if (name.Length == 0 || name == pl.Name)
            return;

        pl.Name = name;
        if (_currentPlaylist == pl)
            ContentTitle.Text = name;
        PersistPlaylists();
    }

    private async Task DeletePlaylistConfirmed(Playlist pl)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = "删除歌单",
            Content = $"确定要删除歌单「{pl.Name}」吗？（不会删除歌曲文件）",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;

        _playlists.Remove(pl);
        if (_currentPlaylist == pl)
        {
            _currentPlaylist = null;
            ShowView(NavView.Local);
        }
        PersistPlaylists();
    }

    // ---------- Playlist membership helpers ----------

    private void AddTracksToPlaylist(Playlist pl, IEnumerable<Track> tracks)
    {
        var added = false;
        foreach (var t in tracks)
        {
            if (!pl.Tracks.Contains(t))
            {
                pl.Tracks.Add(t);
                added = true;
            }
        }
        if (!added)
            return;

        PersistPlaylists();
        if (_currentView == NavView.Playlist && _currentPlaylist == pl)
            RefreshDisplay();
    }

    private void RemoveFromCurrentPlaylist(Track t)
    {
        if (_currentPlaylist == null)
            return;
        if (!_currentPlaylist.Tracks.Remove(t))
            return;

        PersistPlaylists();
        RefreshDisplay();
    }

    private void BtnSelectMode_Checked(object sender, RoutedEventArgs e)
    {
        _selectMode = true;
        ApplySelectionMode();
    }

    private void BtnSelectMode_Unchecked(object sender, RoutedEventArgs e)
    {
        _selectMode = false;
        ApplySelectionMode();
    }

    private void ApplySelectionMode()
    {
        var mode = _selectMode ? ListViewSelectionMode.Multiple : ListViewSelectionMode.None;

        // Clear while the lists still ACCEPT a selection: once SelectionMode
        // is None the SelectedItems collection is disconnected and Clear()
        // throws a COMException (0x8000FFFF).
        if (!_selectMode)
        {
            try { TrackGrid.SelectedItems.Clear(); } catch { }
            try { TrackList.SelectedItems.Clear(); } catch { }
        }

        TrackGrid.SelectionMode = mode;
        TrackList.SelectionMode = mode;
        TrackList.CanReorderItems = CanReorderPlaylist();
        BtnBatchAdd.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>In-place drag reordering of a playlist is only meaningful when
    /// the displayed order IS the playlist order (no search filter, no sort).</summary>
    private bool CanReorderPlaylist() =>
        !_selectMode
        && _currentView == NavView.Playlist
        && _currentPlaylist != null
        && string.IsNullOrEmpty(_searchText)
        && _sortBy == "Default";

    private void BtnBatchAdd_Click(object sender, RoutedEventArgs e)
    {
        var sel = (_viewMode == "Grid" ? TrackGrid.SelectedItems : TrackList.SelectedItems)
            .Cast<Track>().ToList();
        if (sel.Count == 0 || _playlists.Count == 0)
            return;

        var flyout = new MenuFlyout();
        foreach (var pl in _playlists.ToList())
        {
            var target = pl;
            var item = new MenuFlyoutItem { Text = $"{target.Name}（{target.Tracks.Count} 首）" };
            item.Click += (_, _) =>
            {
                AddTracksToPlaylist(target, sel);
                BtnSelectMode.IsChecked = false; // done, leave select mode
            };
            flyout.Items.Add(item);
        }
        flyout.ShowAt(BtnBatchAdd);
    }

    // ---------- Drag & drop (onto the content grid) ----------

    private void TrackGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride != null)
            e.DragUIOverride.Caption = "添加文件或文件夹";
    }

    private async void TrackGrid_Drop(object sender, DragEventArgs e)
    {
        var items = await e.DataView.GetStorageItemsAsync();
        var paths = new List<string>();
        foreach (var item in items)
        {
            if (item is StorageFile file)
            {
                if (FolderScanner.IsAudio(file.Path))
                    paths.Add(file.Path);
            }
            else if (item is StorageFolder folder)
            {
                paths.AddRange(FolderScanner.Scan(folder.Path, recursive: true));
            }
        }

        if (paths.Count == 0)
            return;

        if (_currentView == NavView.Playlist && _currentPlaylist != null)
        {
            foreach (var p in paths)
            {
                var t = ResolveTrack(p);
                if (t != null && !_currentPlaylist.Tracks.Contains(t))
                    _currentPlaylist.Tracks.Add(t);
            }
            PersistPlaylists();
            PersistLibrary();
        }
        else
        {
            AddItemsToLibrary(paths);
        }

        RefreshDisplay();
    }

    // ---------- Card / row interactions ----------

    private void Card_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.FindName("Dim") is Border dim)
                dim.Opacity = 0.32;
            if (fe.FindName("PlayBtn") is Button btn)
                btn.Opacity = 1;
        }
    }

    private void Card_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.FindName("Dim") is Border dim)
                dim.Opacity = 0;
            if (fe.FindName("PlayBtn") is Button btn)
                btn.Opacity = 0;
        }
    }

    private void CardPlay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Track t })
        {
            var idx = _activeTracks.IndexOf(t);
            if (idx >= 0)
                StartPlay(_activeTracks, idx);
        }
    }

    // Row hover: highlight + reveal the "play next" button (FindName resolves
    // named children inside the instantiated DataTemplate, same as the cards).
    private static readonly SolidColorBrush RowHoverBrush =
        new(Windows.UI.Color.FromArgb(255, 0x18, 0x18, 0x22));

    private void TrackRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid row)
            return;
        row.Background = RowHoverBrush;
        if (row.FindName("RowPlayNextBtn") is Button btn)
            btn.Opacity = 1;
    }

    private void TrackRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Grid row)
            return;
        row.Background = null;
        if (row.FindName("RowPlayNextBtn") is Button btn)
            btn.Opacity = 0;
    }

    private void TrackGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_selectMode)
            return; // in multi-select a double click means checking items
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && !(dep is GridViewItem))
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is GridViewItem item && TrackGrid.ItemFromContainer(item) is Track track)
        {
            var idx = _activeTracks.IndexOf(track);
            if (idx >= 0)
                StartPlay(_activeTracks, idx);
        }
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_selectMode)
            return;
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && !(dep is ListViewItem))
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is ListViewItem item && TrackList.ItemFromContainer(item) is Track track)
        {
            var idx = _activeTracks.IndexOf(track);
            if (idx >= 0)
                StartPlay(_activeTracks, idx);
        }
    }

    private void TrackItem_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Track t })
            _contextTrack = t;
        else
            return;

        // Inject view-dependent items into the (template-owned) context menu.
        // Items tagged "dyn" from a previous open are stripped first.
        if (sender is not FrameworkElement fe || fe.ContextFlyout is not MenuFlyout mf)
            return;

        for (var i = mf.Items.Count - 1; i >= 0; i--)
        {
            if ((mf.Items[i].Tag as string) == "dyn")
                mf.Items.RemoveAt(i);
        }

        var insert = 0;
        var playNext = new MenuFlyoutItem
        {
            Text = "下一首播放",
            Tag = "dyn",
            Icon = new FontIcon { Glyph = "\uE101" }
        };
        playNext.Click += (_, _) => PlayTrackNext(t);
        mf.Items.Insert(insert++, playNext);

        var onlineLyric = new MenuFlyoutItem
        {
            Text = "在线搜索歌词...",
            Tag = "dyn",
            Icon = new FontIcon { Glyph = "\uE721" }
        };
        onlineLyric.Click += (_, _) => ShowOnlineLyricDialog(t);
        mf.Items.Insert(insert++, onlineLyric);

        var autoLyric = new MenuFlyoutItem
        {
            Text = "自动下载歌词",
            Tag = "dyn",
            Icon = new FontIcon { Glyph = "\uE896" }
        };
        autoLyric.Click += async (_, _) => await AutoDownloadLyricForTrackAsync(t);
        mf.Items.Insert(insert++, autoLyric);

        if (_currentView == NavView.Playlist && _currentPlaylist != null)
        {
            var rm = new MenuFlyoutItem { Text = $"从「{_currentPlaylist.Name}」移除", Tag = "dyn" };
            rm.Click += (_, _) => RemoveFromCurrentPlaylist(t);
            mf.Items.Insert(insert++, rm);
        }

        if (_currentView is NavView.Local or NavView.Recent && _playlists.Count > 0)
        {
            var sub = new MenuFlyoutSubItem { Text = "添加到歌单", Tag = "dyn" };
            foreach (var pl in _playlists.ToList())
            {
                var target = pl;
                var item = new MenuFlyoutItem { Text = target.Name };
                item.Click += (_, _) => AddTracksToPlaylist(target, new[] { t });
                sub.Items.Add(item);
            }
            mf.Items.Insert(insert, sub);
        }
    }

    // ---------- Manual lyric assignment ----------

    private async void AssignLyric_Click(object sender, RoutedEventArgs e)
    {
        var track = _contextTrack ?? (sender as FrameworkElement)?.DataContext as Track;
        if (track == null)
            return;

        var picker = new FileOpenPicker();
        InitPicker(picker);
        picker.ViewMode = PickerViewMode.List;
        picker.FileTypeFilter.Add(".lrc");
        picker.FileTypeFilter.Add(".srt");
        picker.FileTypeFilter.Add(".txt");

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return;

        track.LyricPath = file.Path;
        LyricBindingStore.Set(track.Path, file.Path);

        if (_currentTrack == track)
            LoadLyricsFor(_loadedIndex);
    }

    private void ClearLyric_Click(object sender, RoutedEventArgs e)
    {
        var track = _contextTrack ?? (sender as FrameworkElement)?.DataContext as Track;
        if (track == null)
            return;

        track.LyricPath = null;
        LyricBindingStore.Clear(track.Path);

        if (_currentTrack == track)
            LoadLyricsFor(_loadedIndex);
    }

    // ---------- "Play next" (insert into the queue after the current track) ----------

    private void TrackPlayNext_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: Track t })
            PlayTrackNext(t);
    }

    /// <summary>
    /// Insert <paramref name="t"/> right after the current track. The queue is
    /// snapshotted into a dedicated ObservableCollection first so the library /
    /// the source playlist are never mutated (and a later duplicate of the
    /// track is skipped so it doesn't play twice).
    /// </summary>
    private void PlayTrackNext(Track t)
    {
        var q = _playback.Queue;
        var cur = _playback.CurrentIndex;

        if (q == null || q.Count == 0 || cur < 0 || cur >= q.Count)
        {
            // Nothing playing yet: make it the (paused) queue head.
            var single = new ObservableCollection<Track> { t };
            _playback.SetQueue(single, 0, autoPlay: false);
            BindQueue();
            return;
        }

        if (q[cur] == t)
            return; // already the current track

        var newQ = new ObservableCollection<Track>();
        for (var i = 0; i < q.Count; i++)
        {
            if (q[i] == t)
                continue; // it will play next instead
            newQ.Add(q[i]);
            if (i == cur)
                newQ.Add(t);
        }

        var newIdx = newQ.IndexOf(q[cur]);
        if (newIdx < 0)
            newIdx = 0;

        _playback.ReplaceQueueSilent(newQ, newIdx);
        BindQueue();
    }

    // ---------- Search ----------

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = SearchBox.Text.Trim();
        RefreshDisplay();
    }

    private void RefreshDisplay()
    {
        if (_activeTracks == null)
            return;

        var q = _searchText;
        var filtered = _activeTracks.Where(t =>
            string.IsNullOrWhiteSpace(q)
            || (t.Title != null && t.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            || (t.Artist != null && t.Artist.Contains(q, StringComparison.OrdinalIgnoreCase))
            || (t.Album != null && t.Album.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();

        filtered = _sortBy switch
        {
            "Title" => filtered.OrderBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            "Artist" => filtered.OrderBy(t => t.Artist ?? "", StringComparer.OrdinalIgnoreCase)
                                  .ThenBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            "Album" => filtered.OrderBy(t => t.Album ?? "", StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            "DateAdded" => filtered.OrderByDescending(t => t.DateAdded).ToList(),
            "Duration" => filtered.OrderBy(t => t.Duration).ToList(),
            _ => filtered
        };

        _displayTracks.Clear();
        foreach (var t in filtered)
            _displayTracks.Add(t);

        TrackList.CanReorderItems = CanReorderPlaylist();

        UpdateEmptyHint();
    }

    private void TrackList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // Only an internal reorder (Move) rewrites the playlist order; a file
        // drop from Explorer arrives as Copy and is handled by TrackGrid_Drop.
        if (args.DropResult != DataPackageOperation.Move)
            return;
        if (_currentView != NavView.Playlist || _currentPlaylist == null)
            return;

        _currentPlaylist.Tracks.Clear();
        foreach (var t in _displayTracks)
            _currentPlaylist.Tracks.Add(t);
        PersistPlaylists();
    }

    private void UpdateEmptyHint()
    {
        if (_currentView == NavView.Settings)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        if (_activeTracks.Count == 0)
        {
            EmptyHint.Text = "这里还没有歌曲，点击上方「添加文件」或「递归文件夹」开始吧";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else if (_displayTracks.Count == 0)
        {
            EmptyHint.Text = "没有匹配的歌曲";
            EmptyHint.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyHint.Visibility = Visibility.Collapsed;
        }
    }

    // ---------- Playing from a list ----------

    private void StartPlay(IList<Track> list, int index)
    {
        _activeTracks = list;
        _playback.SetQueue(list, index);
        BindQueue();
    }

    private void BindQueue()
    {
        if (_playback.Queue != _boundQueue)
        {
            _boundQueue = _playback.Queue;
            QueueList.ItemsSource = _playback.Queue;
        }

        QueueList.SelectedIndex = _playback.CurrentIndex;
    }

    // ---------- Transport ----------

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_playback.Queue == null || _playback.Queue.Count == 0)
        {
            if (_library.Count > 0)
                StartPlay(_library, 0);
            return;
        }

        _playback.PlayPause();
    }

    private void BtnPrev_Click(object sender, RoutedEventArgs e) => _playback.Previous();
    private void BtnNext_Click(object sender, RoutedEventArgs e) => _playback.Next();

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) =>
        _playback.Volume = VolumeSlider.Value / 100.0;

    // The Slider marks its pointer events as handled internally (especially
    // the release after a thumb drag), so XAML-wired handlers never fire and
    // seeking silently does nothing. Register with handledEventsToo: true
    // instead. No CapturePointer either — the slider's own thumb needs it.
    private void WireSeekSlider()
    {
        SeekSlider.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(SeekPointer_Pressed), true);
        SeekSlider.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(SeekPointer_Released), true);
        SeekSlider.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler(SeekPointer_Canceled), true);
    }

    private void SeekPointer_Pressed(object sender, PointerRoutedEventArgs e) => _isSeeking = true;

    private void SeekPointer_Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking)
            return;
        _isSeeking = false;
        _playback.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void SeekPointer_Canceled(object sender, PointerRoutedEventArgs e) => _isSeeking = false;

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isSeeking)
            TimeCurrent.Text = FormatTime(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void BtnPlayMode_Click(object sender, RoutedEventArgs e)
    {
        var idx = Array.IndexOf(_modeOrder, _playback.Mode);
        _playback.Mode = _modeOrder[(idx + 1) % _modeOrder.Length];
        _settings.DefaultPlayMode = _playback.Mode.ToString();
        SettingsStore.Save(_settings);
        ApplyPlayModeLabel();
    }

    private void ApplyPlayModeLabel()
    {
        (string glyph, string tip) = _playback.Mode switch
        {
            PlayMode.Sequential => ("\uE8FD", "顺序播放"),
            PlayMode.LoopAll => ("\uE8EE", "列表循环"),
            PlayMode.LoopOne => ("\uE8ED", "单曲循环"),
            PlayMode.Random => ("\uE8B1", "随机播放"),
            _ => ("\uE8FD", "顺序播放")
        };
        PlayModeIcon.Glyph = glyph;
        ToolTipService.SetToolTip(BtnPlayMode, tip);
    }

    private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Space)
            return;

        // Don't hijack Space when it belongs to the focused control (typing in
        // the search box, pressing a focused button, ...).
        var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement();
        if (focused is TextBox or ComboBox or Slider or ToggleSwitch or Microsoft.UI.Xaml.Controls.Primitives.ButtonBase)
            return;

        BtnPlay_Click(sender, e);
        e.Handled = true;
    }

    // ---------- Now-playing panel toggle ----------

    private void BtnToggleNowPlaying_Click(object sender, RoutedEventArgs e)
    {
        // Switch the whole window to / from the Now-Playing view (like the
        // Local / Recent / Settings navigation), instead of only a side panel.
        ShowView(_currentView == NavView.NowPlaying ? NavView.Local : NavView.NowPlaying);
    }

    // ---------- Queue panel ----------

    private void BtnQueueToggle_Click(object sender, RoutedEventArgs e) =>
        QueuePanel.Visibility = QueuePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void BtnQueueClose_Click(object sender, RoutedEventArgs e) =>
        QueuePanel.Visibility = Visibility.Collapsed;

    private void QueueList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Track track && _playback.Queue != null)
        {
            var idx = _playback.Queue.IndexOf(track);
            if (idx >= 0)
                _playback.MoveTo(idx);
        }
    }

    private void QueueList_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        // Remember which track is "current" before the reorder shuffles indices.
        var q = _playback.Queue;
        var i = _playback.CurrentIndex;
        _queueDragCurrent = (q != null && i >= 0 && i < q.Count) ? q[i] : null;
    }

    private void QueueList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (args.DropResult != DataPackageOperation.Move || _queueDragCurrent == null)
            return;
        if (_playback.Queue is not ObservableCollection<Track> q)
            return;

        var idx = q.IndexOf(_queueDragCurrent);
        if (idx >= 0)
        {
            _playback.SetIndexSilent(idx);
            QueueList.SelectedIndex = idx;
        }
        _queueDragCurrent = null;
    }

    private void QueueRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.DataContext is not Track track)
            return;
        if (_playback.Queue is not ObservableCollection<Track> q)
            return;

        var idx = q.IndexOf(track);
        if (idx < 0)
            return;

        var wasCurrent = idx == _playback.CurrentIndex;
        q.RemoveAt(idx);
        if (idx < _playback.CurrentIndex)
            _playback.ShiftIndex(-1);

        if (wasCurrent)
        {
            if (q.Count > 0)
                _playback.MoveTo(Math.Min(idx, q.Count - 1));
            else
            {
                _playback.Clear();
                _boundQueue = null;
                QueueList.ItemsSource = null;
                ResetNowPlaying();
            }
        }
    }

    // ---------- Desktop lyrics ----------

    private void BtnDesktopLyrics_Checked(object sender, RoutedEventArgs e)
    {
        _desktopLyrics ??= new DesktopLyricsOverlay();
        _desktopLyrics.PositionReported += (x, y) => _dispatcher.TryEnqueue(() =>
        {
            _settings.LyricPosX = x;
            _settings.LyricPosY = y;
            SettingsStore.Save(_settings);
        });
        _desktopLyrics.ApplyStyle(_settings);
        _desktopLyrics.SetClickThrough(_settings.LyricClickThroughDefault);
        // Restore the position the user dragged it to last time (if any).
        if (_settings.LyricPosX >= 0 && _settings.LyricPosY >= 0)
            _desktopLyrics.SetPosition(_settings.LyricPosX, _settings.LyricPosY);
        _desktopLyrics.Activate();
        BtnClickThrough.IsEnabled = true;
        BtnClickThrough.IsChecked = _settings.LyricClickThroughDefault;

        if (_lyrics != null && _currentLineIndex >= 0)
        {
            var l = _lyrics.Lines[_currentLineIndex];
            _desktopLyrics.UpdateLyric(_currentTrack, l.Original ?? string.Empty, l.Romaji, l.Translation);
        }
    }

    private void BtnDesktopLyrics_Unchecked(object sender, RoutedEventArgs e)
    {
        _desktopLyrics?.Close();
        _desktopLyrics = null;
        BtnClickThrough.IsEnabled = false;
        BtnClickThrough.IsChecked = false;
    }

    private void BtnClickThrough_Checked(object sender, RoutedEventArgs e) =>
        _desktopLyrics?.SetClickThrough(true);

    private void BtnClickThrough_Unchecked(object sender, RoutedEventArgs e) =>
        _desktopLyrics?.SetClickThrough(false);

    // ---------- Playback events ----------

    private void OnStateChanged(MediaPlaybackState state)
    {
        _isPlaying = state == MediaPlaybackState.Playing;
        BtnPlay.Content = new FontIcon { Glyph = _isPlaying ? "\uE103" : "\uE102", FontSize = 18 };

        if (state == MediaPlaybackState.Playing)
        {
            _consecutiveFailures = 0;

            // Playback actually started for the current index — only now does
            // it qualify for "recently played" (covers the restored session).
            var idx = _playback.CurrentIndex;
            var q = _playback.Queue;
            if (q != null && idx >= 0 && idx < q.Count && idx != _lastRecentIndex)
            {
                _lastRecentIndex = idx;
                var t = q[idx];
                t.LastPlayed = DateTime.Now;
                PushRecent(t);
            }
        }
    }

    private void OnCurrentIndexChanged(int index)
    {
        // During session restore the index changes without any playback; the
        // recent entry is then made from OnStateChanged once playing begins.
        if (_suppressNextRecent)
        {
            _suppressNextRecent = false;
        }
        else
        {
            var q = _playback.Queue;
            if (q != null && index >= 0 && index < q.Count && index != _lastRecentIndex)
            {
                _lastRecentIndex = index;
                var t = q[index];
                t.LastPlayed = DateTime.Now;
                PushRecent(t);
            }
        }

        LoadLyricsFor(index);

        QueueList.SelectedIndex = _playback.CurrentIndex;
    }

    private void OnPlaybackMediaFailed(string message)
    {
        var src = _playback.Queue;
        var name = "未知曲目";
        if (src != null && _playback.CurrentIndex >= 0 && _playback.CurrentIndex < src.Count)
            name = src[_playback.CurrentIndex]?.Title ?? name;

        PlayErrorBar.Severity = InfoBarSeverity.Error;
        PlayErrorBar.Title = "无法播放";
        PlayErrorBar.Message = $"{name}{(string.IsNullOrEmpty(message) ? "" : $"（{message}）")}，已跳过";
        PlayErrorBar.IsOpen = true;
        _errorBarTimer.Stop();
        _errorBarTimer.Start();

        var q = _playback.Queue;
        if (q != null && q.Count > 0 && ++_consecutiveFailures < q.Count)
            _playback.Next(); // auto-skip the broken track
        else
            _playback.Pause(); // everything failed — stop instead of looping
    }

    private void OnPositionTick(TimeSpan pos)
    {
        var dur = _playback.Duration;
        if (dur.TotalSeconds > 0 && Math.Abs(SeekSlider.Maximum - dur.TotalSeconds) > 1)
            SeekSlider.Maximum = dur.TotalSeconds;

        if (!_isSeeking)
            SeekSlider.Value = pos.TotalSeconds;

        TimeCurrent.Text = FormatTime(pos);
        if (dur.TotalSeconds > 0)
            TimeTotal.Text = FormatTime(dur);

        UpdateLyricHighlight(pos);

        if ((DateTime.Now - _lastProgressSave).TotalSeconds >= 3)
        {
            _lastProgressSave = DateTime.Now;
            PlaylistStore.SaveProgress(_playback.CurrentIndex, pos, CurrentPath());
        }
    }

    private string? CurrentPath()
    {
        var q = _playback.Queue;
        if (q != null && _playback.CurrentIndex >= 0 && _playback.CurrentIndex < q.Count)
            return q[_playback.CurrentIndex]?.Path;
        return null;
    }

    // ---------- Lyrics ----------

    private void LoadLyricsFor(int index)
    {
        _loadedIndex = index;
        _currentLineIndex = -1;
        LyricStack.Children.Clear();
        _lyricPanels.Clear();

        var src = _playback.Queue as IList<Track> ?? _library;

        if (index < 0 || index >= src.Count)
        {
            ResetNowPlaying();
            _lyrics = null;
            return;
        }

        var track = src[index];
        NowTitle.Text = track.Title;
        NowArtist.Text = track.Artist;
        NowAlbum.Text = string.IsNullOrWhiteSpace(track.Album) ? string.Empty : "专辑：" + track.Album;
        ApplyCover(track.Cover);
        MiniCover.Source = track.Cover;
        MiniTitle.Text = track.Title;
        MiniArtist.Text = track.Artist;

        BindCurrentCover(track);

        _lyrics = LyricsParser.Parse(track.Path, track.LyricPath,
            _settings.LyricEncoding == "auto" ? null : _settings.LyricEncoding);
        if (_lyrics == null || _lyrics.Lines.Count == 0)
        {
            LyricStack.Children.Add(new TextBlock
            {
                Text = "暂无歌词\n右键歌曲 →「在线搜索歌词」可在线下载",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            });
            _lyrics = null;
            return;
        }

        BuildLyricUI(_lyrics);
    }

    private void BindCurrentCover(Track track)
    {
        if (_currentTrack != null)
            _currentTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

        _currentTrack = track;
        _currentTrack.PropertyChanged += CurrentTrack_PropertyChanged;
    }

    private void CurrentTrack_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Track track || e.PropertyName != nameof(Track.Cover))
            return;
        ApplyCover(track.Cover);
        MiniCover.Source = track.Cover;
    }

    private void ApplyCover(ImageSource? cover)
    {
        NowCoverEllipse.Fill = cover == null
            ? CoverPlaceholder
            : new ImageBrush { ImageSource = cover };
    }

    private void ResetNowPlaying()
    {
        NowTitle.Text = "未在播放";
        NowArtist.Text = string.Empty;
        NowAlbum.Text = string.Empty;
        ApplyCover(null);
        MiniCover.Source = null;
        MiniTitle.Text = "未在播放";
        MiniArtist.Text = string.Empty;
        TimeCurrent.Text = "00:00";
        TimeTotal.Text = "00:00";
        SeekSlider.Value = 0;
    }

    private void BuildLyricUI(LyricDocument doc)
    {
        var showRoma = _settings.LyricShowRomaji;
        var showTrans = _settings.LyricShowTranslation;

        foreach (var line in doc.Lines)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 6, 0, 6),
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            if (!string.IsNullOrWhiteSpace(line.Original))
                panel.Children.Add(MakeTextBlock(line.Original, 22, Microsoft.UI.Colors.White));
            if (showRoma && !string.IsNullOrWhiteSpace(line.Romaji))
                panel.Children.Add(MakeTextBlock(line.Romaji, 14, Microsoft.UI.Colors.SkyBlue));
            if (showTrans && !string.IsNullOrWhiteSpace(line.Translation))
                panel.Children.Add(MakeTextBlock(line.Translation, 16, Microsoft.UI.Colors.LightGreen));

            if (panel.Children.Count == 0)
                panel.Children.Add(MakeTextBlock("♪", 18, Microsoft.UI.Colors.Gray));

            LyricStack.Children.Add(panel);
            _lyricPanels.Add(panel);
        }
    }

    /// <summary>Rebuild the lyrics panel (e.g. after toggling romaji/translation)
    /// and re-highlight + re-push the current line to the desktop overlay.</summary>
    private void RebuildLyricUi()
    {
        LyricStack.Children.Clear();
        _lyricPanels.Clear();
        _currentLineIndex = -1;

        if (_lyrics == null)
            return;

        BuildLyricUI(_lyrics);
        UpdateLyricHighlight(_playback.Position);
    }

    private static TextBlock MakeTextBlock(string text, double size, Windows.UI.Color color)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = new SolidColorBrush(color),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Opacity = 0.45
        };
    }

    private void UpdateLyricHighlight(TimeSpan pos)
    {
        if (_lyrics == null || _lyricPanels.Count == 0)
            return;

        // User calibration: positive offset = lyrics appear later.
        var t = pos - TimeSpan.FromMilliseconds(_settings.LyricOffsetMs);

        var idx = -1;
        for (var i = 0; i < _lyrics.Lines.Count; i++)
        {
            if (_lyrics.Lines[i].Time <= t)
                idx = i;
            else
                break;
        }

        if (idx == _currentLineIndex)
        {
            PushDesktop(idx);
            return;
        }

        if (_currentLineIndex >= 0 && _currentLineIndex < _lyricPanels.Count)
            SetLineActive(_lyricPanels[_currentLineIndex], false);

        _currentLineIndex = idx;

        if (idx >= 0 && idx < _lyricPanels.Count)
        {
            SetLineActive(_lyricPanels[idx], true);
            var tb = _lyricPanels[idx];
            LyricScroll.ChangeView(null, tb.ActualOffset.Y - LyricScroll.ActualHeight / 2 + tb.ActualHeight / 2, null);
        }

        PushDesktop(idx);
    }

    private void PushDesktop(int idx)
    {
        if (_desktopLyrics == null || idx < 0 || _lyrics == null)
            return;
        var line = _lyrics.Lines[idx];
        var roma = _settings.LyricShowRomaji ? line.Romaji : null;
        var trans = _settings.LyricShowTranslation ? line.Translation : null;
        _desktopLyrics.UpdateLyric(_currentTrack, line.Original ?? string.Empty, roma, trans);
    }

    // ---------- Romaji / translation toggles (in-app + desktop overlay) ----------

    private void LyricRomajiToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (LyricRomajiToggle.IsChecked == true == _settings.LyricShowRomaji)
            return;
        _settings.LyricShowRomaji = LyricRomajiToggle.IsChecked == true;
        SettingsStore.Save(_settings);
        RebuildLyricUi();
    }

    private void LyricTransToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (LyricTransToggle.IsChecked == true == _settings.LyricShowTranslation)
            return;
        _settings.LyricShowTranslation = LyricTransToggle.IsChecked == true;
        SettingsStore.Save(_settings);
        RebuildLyricUi();
    }

    // ---------- Lyric offset calibration ----------

    private void AdjustLyricOffset(int deltaMs)
    {
        _settings.LyricOffsetMs = Math.Clamp(_settings.LyricOffsetMs + deltaMs, -10000, 10000);
        SettingsStore.Save(_settings);
        UpdateLyricOffsetText();
        UpdateLyricHighlight(_playback.Position);
    }

    private void UpdateLyricOffsetText()
    {
        var v = _settings.LyricOffsetMs / 1000.0;
        LyricOffsetText.Text = $"{v:+0.0;-0.0;0.0}s";
    }

    private void BtnLyricOffsetDown_Click(object sender, RoutedEventArgs e) => AdjustLyricOffset(-500);

    private void BtnLyricOffsetUp_Click(object sender, RoutedEventArgs e) => AdjustLyricOffset(500);

    private void BtnLyricOffsetReset_Click(object sender, RoutedEventArgs e)
    {
        _settings.LyricOffsetMs = 0;
        SettingsStore.Save(_settings);
        UpdateLyricOffsetText();
        UpdateLyricHighlight(_playback.Position);
    }

    // ---------- Online lyrics (QQ Music) ----------

    /// <summary>
    /// Keyword for QQ Music search. QQ-downloaded files follow
    /// "歌手1_歌手2... - 歌曲名.ext"; Track() already splits that into
    /// Artist="歌手1_歌手2" / Title="歌名", so just join (underscores -> spaces).
    /// </summary>
    private static string BuildSearchKeyword(Track t)
    {
        var artist = t.Artist == "未知歌手" ? "" : t.Artist.Replace('_', ' ');
        return $"{artist} {t.Title}".Trim();
    }

    /// <summary>Normalize for fuzzy title/artist comparison (letters/digits only).</summary>
    private static string Norm(string? s) =>
        new((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>Does the track already have a usable lyric file (binding / .lrc / .srt)?</summary>
    private static bool HasLocalLyric(Track t)
    {
        if (!string.IsNullOrEmpty(LyricBindingStore.Get(t.Path)))
            return true;
        if (!string.IsNullOrEmpty(t.LyricPath) && File.Exists(t.LyricPath))
            return true;

        var basePath = Path.Combine(
            Path.GetDirectoryName(t.Path) ?? "",
            Path.GetFileNameWithoutExtension(t.Path));
        return File.Exists(basePath + ".lrc") || File.Exists(basePath + ".srt");
    }

    /// <summary>
    /// Write downloaded lyrics next to the audio: main .lrc plus companion
    /// .zh.lrc (translation) / .romaji.lrc — the exact names LyricsParser
    /// auto-detects and merges by timestamp.
    /// </summary>
    private static void SaveLyricFiles(Track track, string lyric, string? trans, string? roma)
    {
        var basePath = Path.Combine(
            Path.GetDirectoryName(track.Path) ?? "",
            Path.GetFileNameWithoutExtension(track.Path));
        var utf8 = new System.Text.UTF8Encoding(false);

        AtomicFile.WriteAllText(basePath + ".lrc", lyric, utf8);
        if (!string.IsNullOrWhiteSpace(trans))
            AtomicFile.WriteAllText(basePath + ".zh.lrc", trans, utf8);
        if (!string.IsNullOrWhiteSpace(roma))
            AtomicFile.WriteAllText(basePath + ".romaji.lrc", roma, utf8);
    }

    private async Task AutoDownloadLyricForTrackAsync(Track track)
    {
        ShowInfoBar($"正在为「{track.Title}」匹配歌词…");
        try
        {
            if (await TryAutoDownloadAsync(track))
            {
                if (_currentTrack == track)
                    LoadLyricsFor(_loadedIndex);
                ShowInfoBar($"已下载歌词：{track.Title}");
            }
            else
            {
                ShowInfoBar($"未找到匹配的歌词：{track.Title}");
            }
        }
        catch (Exception ex)
        {
            ShowInfoBar($"歌词保存失败：{ex.Message}（歌曲目录可能只读或无写入权限）");
        }
    }

    /// <summary>
    /// Search by "artist title" and download lyrics.
    /// NetEase first — it serves original + translation + romaji from ONE
    /// source, so companion-file timestamps line up exactly. QQ Music is the
    /// fallback (original lyric only: its web API stopped serving
    /// translations without login).
    /// </summary>
    private async Task<bool> TryAutoDownloadAsync(Track track)
    {
        var keyword = BuildSearchKeyword(track);
        if (keyword.Length == 0)
            return false;

        var localTitle = Norm(track.Title);
        if (localTitle.Length == 0)
            return false;

        int? durationSec = track.Duration > TimeSpan.Zero ? (int)track.Duration.TotalSeconds : null;

        // 1) NetEase (full three-line set).
        var neSong = await MatchNetEaseAsync(keyword, track.Title, durationSec);
        if (neSong != null)
        {
            var ne = await NetEaseLyricService.FetchLyricAsync(neSong.SongMid);
            if (!string.IsNullOrEmpty(ne?.Lyric))
            {
                SaveLyricFiles(track, ne.Value.Lyric!, ne.Value.Trans, ne.Value.Roma);
                return true;
            }
        }

        // 2) QQ Music fallback (original only).
        var results = await QQLyricService.SearchAsync(keyword, 20);
        if (results.Count == 0)
            return false;

        var primaryArtist = Norm(track.Artist == "未知歌手" ? "" : track.Artist);

        QQSong? best = null;
        var bestScore = int.MinValue;

        foreach (var r in results)
        {
            var rt = Norm(r.Title);
            if (rt.Length == 0)
                continue;

            int score;
            if (rt == localTitle)
                score = 10;
            else if (rt.Contains(localTitle, StringComparison.Ordinal) ||
                     localTitle.Contains(rt, StringComparison.Ordinal))
                score = 6;
            else
                continue; // title must match at least loosely

            if (primaryArtist.Length > 0 && Norm(r.Artist).Contains(primaryArtist, StringComparison.Ordinal))
                score += 5;

            if (durationSec is > 0 && r.DurationSec > 0)
            {
                var delta = Math.Abs(durationSec.Value - r.DurationSec);
                if (delta <= 3) score += 8;
                else if (delta <= 8) score += 3;
                else score -= 2;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }

        // Require a real title match (>=10) to avoid saving wrong lyrics.
        if (best == null || bestScore < 10)
            return false;

        var lyric = await QQLyricService.FetchLyricAsync(best.SongMid);
        if (string.IsNullOrEmpty(lyric?.Lyric))
            return false;

        SaveLyricFiles(track, lyric.Value.Lyric!, lyric.Value.Trans, lyric.Value.Roma);
        return true;
    }

    /// <summary>Pick the best NetEase match: loose title match + closest duration.</summary>
    private async Task<QQSong?> MatchNetEaseAsync(string keyword, string localTitle, int? durationSec)
    {
        var results = await NetEaseLyricService.SearchAsync(keyword, 20);
        if (results.Count == 0)
            return null;

        var nt = Norm(localTitle);
        if (nt.Length == 0)
            return null;

        QQSong? best = null;
        var bestDelta = int.MaxValue;

        foreach (var r in results)
        {
            var rt = Norm(r.Title);
            if (rt.Length == 0)
                continue;

            var titleOk = rt == nt ||
                          rt.Contains(nt, StringComparison.Ordinal) ||
                          nt.Contains(rt, StringComparison.Ordinal);
            if (!titleOk)
                continue;

            if (durationSec is > 0 && r.DurationSec > 0)
            {
                var delta = Math.Abs(durationSec.Value - r.DurationSec);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = r;
                }
            }
            else if (best == null)
            {
                best = r; // no duration info — first loose match
            }
        }

        // With duration info, reject matches more than 5s off.
        if (best != null && bestDelta != int.MaxValue && bestDelta > 5)
            return null;
        return best;
    }

    // ---------- cross-source lyric timestamp alignment ----------

    private static readonly System.Text.RegularExpressions.Regex LyricTimeTagRegex =
        new(@"\[(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>All timestamps appearing in an LRC text (the "master" grid).</summary>
    private static List<TimeSpan> ParseLyricTimes(string lrc)
    {
        var times = new List<TimeSpan>();
        foreach (System.Text.RegularExpressions.Match m in LyricTimeTagRegex.Matches(lrc))
            times.Add(ParseTagTime(m));
        return times;
    }

    /// <summary>
    /// Re-time a companion lyric (translation / romaji) from another source so
    /// its lines merge with the main lyric: each line snaps to the nearest
    /// master timestamp (within 2.5s) — LyricsParser merges by exact time.
    /// Lines with no close master timestamp are dropped.
    /// </summary>
    private static string? SnapToMaster(string? companion, List<TimeSpan> master)
    {
        if (string.IsNullOrWhiteSpace(companion) || master.Count == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        foreach (var raw in companion.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var matches = LyricTimeTagRegex.Matches(line);
            if (matches.Count == 0)
                continue;

            var body = LyricTimeTagRegex.Replace(line, "").Trim();
            if (body.Length == 0)
                continue;

            var t = ParseTagTime(matches[0]);

            var bestDelta = double.MaxValue;
            var bestTime = TimeSpan.Zero;
            foreach (var m in master)
            {
                var d = Math.Abs((m - t).TotalSeconds);
                if (d < bestDelta)
                {
                    bestDelta = d;
                    bestTime = m;
                }
            }

            if (bestDelta > 2.5)
                continue;

            sb.Append('[')
              .Append($"{(int)bestTime.TotalMinutes:D2}:{bestTime.Seconds:D2}.{bestTime.Milliseconds:D3}")
              .Append(']')
              .AppendLine(body);
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static TimeSpan ParseTagTime(System.Text.RegularExpressions.Match m)
    {
        var minutes = int.Parse(m.Groups[1].Value);
        var seconds = int.Parse(m.Groups[2].Value);
        var frac = m.Groups[3].Value;
        var ms = frac.Length switch
        {
            0 => 0,
            1 => int.Parse(frac) * 100,
            2 => int.Parse(frac) * 10,
            _ => int.Parse(frac)
        };
        return new TimeSpan(0, 0, minutes, seconds, ms);
    }

    private async void ShowOnlineLyricDialog(Track track)
    {
        var keywordBox = new TextBox { Text = BuildSearchKeyword(track), Width = 290 };
        var status = new TextBlock
        {
            Text = "输入关键词搜索，选择结果后点击“下载”",
            FontSize = 12,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
            TextWrapping = TextWrapping.Wrap
        };
        var list = new ListView { Height = 320, SelectionMode = ListViewSelectionMode.Single };
        var searchBtn = new Button { Content = "搜索" };
        QQSong? selected = null;

        ContentDialog? dialogRef = null;

        async void DoSearch()
        {
            var kw = (keywordBox.Text ?? "").Trim();
            if (kw.Length == 0)
                return;

            searchBtn.IsEnabled = false;
            status.Text = "搜索中…";
            var results = await QQLyricService.SearchAsync(kw);

            list.Items.Clear();
            foreach (var r in results)
            {
                var dur = r.DurationSec > 0 ? $"{r.DurationSec / 60}:{r.DurationSec % 60:D2}" : "";
                var item = new StackPanel { Spacing = 2, Tag = r, Margin = new Thickness(0, 4, 0, 4) };
                item.Children.Add(new TextBlock
                {
                    Text = r.Title,
                    FontSize = 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.White)
                });
                item.Children.Add(new TextBlock
                {
                    Text = string.Join(" · ", new[] { r.Artist, r.Album, dur }.Where(x => x.Length > 0)),
                    FontSize = 12,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray)
                });
                list.Items.Add(item);
            }

            status.Text = results.Count == 0
                ? "无结果，试试只输入歌曲名"
                : $"共 {results.Count} 条结果";
            searchBtn.IsEnabled = true;
        }

        searchBtn.Click += (_, _) => DoSearch();
        keywordBox.KeyDown += (_, ke) =>
        {
            if (ke.Key == Windows.System.VirtualKey.Enter)
                DoSearch();
        };
        list.SelectionChanged += (_, _) =>
        {
            selected = (list.SelectedItem as FrameworkElement)?.Tag as QQSong;
            if (dialogRef != null)
                dialogRef.IsPrimaryButtonEnabled = selected != null;
        };

        var dialog = new ContentDialog
        {
            XamlRoot = this.Content.XamlRoot,
            Title = $"搜索歌词 — {track.Title}",
            PrimaryButtonText = "下载",
            IsPrimaryButtonEnabled = false,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Width = 380,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Children = { keywordBox, searchBtn }
                    },
                    status,
                    list
                }
            }
        };
        dialogRef = dialog;

        DoSearch(); // fire the initial search while the dialog opens

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || selected == null)
            return;

        ShowInfoBar($"正在下载歌词：{selected.Title} - {selected.Artist}");
        var lyric = await QQLyricService.FetchLyricAsync(selected.SongMid);
        if (string.IsNullOrEmpty(lyric?.Lyric))
        {
            ShowInfoBar("该歌曲没有可用歌词。");
            return;
        }

        track.LyricPath = null; // drop an old manual binding; auto-detect the new file
        string? extraNote = null;
        try
        {
            SaveLyricFiles(track, lyric.Value.Lyric!, lyric.Value.Trans, lyric.Value.Roma);

            // QQ's web API no longer serves translations — top up translation /
            // romaji from NetEase, snapping their timestamps onto the saved
            // main lyric so the three lines merge correctly.
            var master = ParseLyricTimes(lyric.Value.Lyric!);
            var neSong = await MatchNetEaseAsync(
                $"{selected.Title} {selected.Artist}".Trim(),
                selected.Title,
                selected.DurationSec > 0 ? selected.DurationSec : null);
            if (neSong != null)
            {
                var ne = await NetEaseLyricService.FetchLyricAsync(neSong.SongMid);
                var trans = SnapToMaster(ne?.Trans, master);
                var roma = SnapToMaster(ne?.Roma, master);
                if (!string.IsNullOrEmpty(trans) || !string.IsNullOrEmpty(roma))
                {
                    var basePath = Path.Combine(
                        Path.GetDirectoryName(track.Path) ?? "",
                        Path.GetFileNameWithoutExtension(track.Path));
                    var utf8 = new System.Text.UTF8Encoding(false);
                    if (!string.IsNullOrEmpty(trans))
                        AtomicFile.WriteAllText(basePath + ".zh.lrc", trans!, utf8);
                    if (!string.IsNullOrEmpty(roma))
                        AtomicFile.WriteAllText(basePath + ".romaji.lrc", roma!, utf8);
                    extraNote = trans != null && roma != null ? "（含翻译和罗马音）"
                        : trans != null ? "（含翻译）" : "（含罗马音）";
                }
            }
        }
        catch (Exception ex)
        {
            ShowInfoBar($"歌词保存失败：{ex.Message}（歌曲目录可能只读或无写入权限）");
            return;
        }
        if (_currentTrack == track)
            LoadLyricsFor(_loadedIndex);
        ShowInfoBar($"已保存歌词：{selected.Title} - {selected.Artist}{extraNote}");
    }

    // ---------- Batch lyric download ----------

    private bool _batchLyricRunning;

    private async void BtnBatchLyrics_Click(object sender, RoutedEventArgs e)
    {
        if (_batchLyricRunning)
            return;

        var targets = _activeTracks.Where(t => !HasLocalLyric(t)).ToList();
        if (targets.Count == 0)
        {
            ShowInfoBar("所有歌曲都已有歌词。");
            return;
        }

        _batchLyricRunning = true;
        ShowInfoBar($"开始为 {targets.Count} 首缺少歌词的歌曲下载 QQ 音乐歌词…");

        var ok = 0;
        var refreshCurrent = false;
        foreach (var t in targets)
        {
            try
            {
                if (await TryAutoDownloadAsync(t))
                {
                    ok++;
                    if (_currentTrack == t)
                        refreshCurrent = true;
                }
            }
            catch
            {
                // keep going — one bad file shouldn't stop the batch
            }
            await Task.Delay(400); // be polite to the API
        }

        _batchLyricRunning = false;
        if (refreshCurrent)
            LoadLyricsFor(_loadedIndex);
        ShowInfoBar($"歌词下载完成：成功 {ok} / {targets.Count}（失败的通常是重命名或冷门歌曲，可用右键“在线搜索歌词”手动选择）。");
    }

    private void ShowInfoBar(string message)
    {
        PlayErrorBar.Severity = InfoBarSeverity.Informational;
        PlayErrorBar.Title = null;
        PlayErrorBar.Message = message;
        PlayErrorBar.IsOpen = true;
        _errorBarTimer.Stop();
        _errorBarTimer.Start();
    }

    private static void SetLineActive(StackPanel panel, bool active)
    {
        foreach (var child in panel.Children)
        {
            if (child is TextBlock tb)
            {
                tb.Opacity = active ? 1.0 : 0.45;
                tb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    // ---------- Settings ----------

    private void ShowSettings()
    {
        LyricFontSlider.Value = _settings.LyricFontSize;
        UpdateLyricSizePreview();
        LyricColorPicker.Color = ParseHex(_settings.LyricColor);
        LyricOpacitySlider.Value = _settings.LyricBgOpacity * 100;
        LyricBoldToggle.IsOn = _settings.LyricBold;
        LyricAlignCombo.SelectedIndex = _settings.LyricAlign == "Left" ? 1 : 0;
        LyricClickThroughToggle.IsOn = _settings.LyricClickThroughDefault;
        LyricEncodingCombo.SelectedIndex = _settings.LyricEncoding switch
        {
            "gbk" => 1,
            "shift_jis" => 2,
            "big5" => 3,
            "utf-8" => 4,
            _ => 0
        };
        CoverSpinToggle.IsOn = _settings.CoverSpin;
        CloseActionCombo.SelectedIndex = _settings.CloseAction == "Tray" ? 1 : 0;
        AccentColorPicker.Color = ParseHex(string.IsNullOrEmpty(_settings.AccentColor) ? "#31c27c" : _settings.AccentColor);

        // Data/cache location.
        CacheDirBox.Text = DataLocation.Root;
        CacheDirStatus.Text = DataLocation.IsCustom
            ? "当前为自定义位置（默认：%LOCALAPPDATA%\\MusicPlayer）。"
            : "当前使用默认位置：%LOCALAPPDATA%\\MusicPlayer。";
        CacheDirStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)RootGrid.Resources["TextSecondary"];
    }

    private void LyricEncodingCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.LyricEncoding = LyricEncodingCombo.SelectedIndex switch
        {
            1 => "gbk",
            2 => "shift_jis",
            3 => "big5",
            4 => "utf-8",
            _ => "auto"
        };
        SettingsStore.Save(_settings);

        // Re-read the current lyrics with the new encoding, if any are shown.
        if (_loadedIndex >= 0)
            LoadLyricsFor(_loadedIndex);
    }

    private void CloseActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.CloseAction = CloseActionCombo.SelectedIndex == 1 ? "Tray" : "Exit";
        SettingsStore.Save(_settings);
    }

    private async void CacheDirBrowse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.ComputerFolder,
            };
            picker.FileTypeFilter.Add("*");

            InitPicker(picker);

            var folder = await picker.PickSingleFolderAsync();
            if (folder != null)
                CacheDirBox.Text = folder.Path;
        }
        catch
        {
            // best-effort
        }
    }

    private void CacheDirApply_Click(object sender, RoutedEventArgs e)
    {
        var input = (CacheDirBox.Text ?? "").Trim();
        var oldRoot = DataLocation.Root;

        // Empty input -> revert to default location.
        if (string.IsNullOrWhiteSpace(input))
        {
            input = DataLocation.DefaultRoot;
        }
        else if (!Path.IsPathRooted(input))
        {
            CacheDirStatus.Text = "路径无效：请输入绝对路径（如 D:\\MusicPlayerData）。";
            CacheDirStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xe6, 0x5a, 0x5a));
            return;
        }

        try
        {
            Directory.CreateDirectory(input);
        }
        catch (Exception ex)
        {
            CacheDirStatus.Text = $"无法创建目录：{ex.Message}";
            CacheDirStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xe6, 0x5a, 0x5a));
            return;
        }

        var newRoot = input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (!string.Equals(newRoot, oldRoot, StringComparison.OrdinalIgnoreCase))
        {
            // Migrate existing bulk data from the old root to the new one.
            // settings.json intentionally stays in the default root.
            MigrateDataDir(oldRoot, newRoot);
            DataLocation.Apply(newRoot);
        }

        _settings.CacheDir = string.Equals(newRoot, DataLocation.DefaultRoot, StringComparison.OrdinalIgnoreCase)
            ? ""
            : newRoot;
        SettingsStore.Save(_settings);
        CacheDirBox.Text = DataLocation.Root;

        CacheDirStatus.Text = DataLocation.IsCustom
            ? "已切换到自定义位置，现有数据已迁移。"
            : "已恢复为默认位置，现有数据已迁移。";
        CacheDirStatus.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x31, 0xc2, 0x7c));
    }

    private void CacheDirReset_Click(object sender, RoutedEventArgs e)
    {
        CacheDirBox.Text = DataLocation.DefaultRoot;
    }

    /// <summary>
    /// Move the bulk data files (playlist/recent/playlists/progress/lyric bindings)
    /// from <paramref name="oldRoot"/> to <paramref name="newRoot"/>. If a file already
    /// exists in the new location, the newer one wins (we never clobber data).
    /// </summary>
    private static void MigrateDataDir(string oldRoot, string newRoot)
    {
        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
            return;

        var files = new[]
        {
            "playlist.json", "recent.json", "playlists.json", "progress.json", "lyricbindings.json",
        };

        try { Directory.CreateDirectory(newRoot); } catch { /* best-effort */ }

        foreach (var name in files)
        {
            var src = Path.Combine(oldRoot, name);
            var dst = Path.Combine(newRoot, name);
            if (!File.Exists(src))
                continue;

            try
            {
                if (File.Exists(dst))
                {
                    // Keep the newer of the two to avoid data loss.
                    var srcTime = File.GetLastWriteTimeUtc(src);
                    var dstTime = File.GetLastWriteTimeUtc(dst);
                    if (srcTime <= dstTime)
                    {
                        File.Delete(src);
                        continue;
                    }
                }
                File.Move(src, dst, overwrite: true);
            }
            catch
            {
                // best-effort; leave files where they are
            }
        }
    }

    private void AccentColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        _settings.AccentColor = ToHex(args.NewColor);
        SettingsStore.Save(_settings);
        ApplyAccentColor();
    }

    /// <summary>Applies the chosen theme color live by retinting the shared accent brushes
    /// ("QqGreen" solid + "AccentGlow" gradient first stop) that the whole UI references.</summary>
    private void ApplyAccentColor()
    {
        var color = ParseHex(string.IsNullOrEmpty(_settings.AccentColor) ? "#31c27c" : _settings.AccentColor);
        if (RootGrid.Resources["QqGreen"] is SolidColorBrush qq)
            qq.Color = color;
        if (RootGrid.Resources["AccentGlow"] is LinearGradientBrush glow && glow.GradientStops.Count > 0)
            glow.GradientStops[0].Color = color;
    }

    private bool _coverCollapsed;
    private void BtnCollapseCover_Click(object sender, RoutedEventArgs e)
    {
        _coverCollapsed = !_coverCollapsed;
        // Fold the cover (and the song/artist/album block) upward; the lyrics
        // scroll area (Grid.Row 3, height *) grows to fill the freed space.
        CoverDisc.Visibility = _coverCollapsed ? Visibility.Collapsed : Visibility.Visible;
        NowInfoPanel.Visibility = _coverCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CoverChevron.Glyph = _coverCollapsed ? "\uE74F" : "\uE74E"; // down to expand, up to fold
    }

    private void LyricFontSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _settings.LyricFontSize = e.NewValue;
        SettingsStore.Save(_settings);
        UpdateLyricSizePreview();
        ApplyStyleLive();
    }

    /// <summary>Preview panel mirrors the overlay's relative line sizes
    /// (main 1.0x, romaji 0.55x, translation 0.65x — see the C++ overlay).</summary>
    private void UpdateLyricSizePreview()
    {
        var size = _settings.LyricFontSize;
        LyricFontSizeText.Text = ((int)size).ToString();
        LyricSizePreviewMain.FontSize = size;
        LyricSizePreviewRoma.FontSize = Math.Max(9, size * 0.55);
    }

    private void LyricColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        _settings.LyricColor = ToHex(args.NewColor);
        SettingsStore.Save(_settings);
        ApplyStyleLive();
    }

    private void LyricOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _settings.LyricBgOpacity = e.NewValue / 100.0;
        SettingsStore.Save(_settings);
        ApplyStyleLive();
    }

    private void LyricBoldToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.LyricBold = ((ToggleSwitch)sender).IsOn;
        SettingsStore.Save(_settings);
        ApplyStyleLive();
    }

    private void LyricAlignCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.LyricAlign = LyricAlignCombo.SelectedIndex == 1 ? "Left" : "Center";
        SettingsStore.Save(_settings);
        ApplyStyleLive();
    }

    private void LyricClickThroughToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.LyricClickThroughDefault = ((ToggleSwitch)sender).IsOn;
        SettingsStore.Save(_settings);
    }

    private void CoverSpinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.CoverSpin = ((ToggleSwitch)sender).IsOn;
        SettingsStore.Save(_settings);
        if (!_settings.CoverSpin)
            _discRotate.Angle = 0;
    }

    private void ApplyStyleLive()
    {
        _desktopLyrics?.ApplyStyle(_settings);
    }

    // ---------- Window lifecycle ----------

    private void MainWindow_Activated(object? sender, WindowActivatedEventArgs e)
    {
        if (_sized)
            return;
        _sized = true;

        var hwnd = WindowNative.GetWindowHandle(this);

        // Virtual screen = union of all monitors, so a position saved on a
        // secondary display is restored there instead of being dragged back
        // onto the primary one.
        var vx = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        var vy = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        var vw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        var vh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        int w = _settings.WindowW > 0 ? Math.Min(_settings.WindowW, vw) : 1380;
        int h = _settings.WindowH > 0 ? Math.Min(_settings.WindowH, vh) : 860;

        int x = _settings.WindowX >= 0 ? _settings.WindowX : vx + (vw - w) / 2;
        int y = _settings.WindowY >= 0 ? _settings.WindowY : vy + (vh - h) / 2;
        x = Math.Max(vx, Math.Min(x, vx + vw - w));
        y = Math.Max(vy, Math.Min(y, vy + vh - h));

        NativeMethods.MoveWindow(hwnd, x, y, w, h, true);
    }

    private void MainWindow_Closed(object? sender, WindowEventArgs e)
    {
        // "最小化到托盘": cancel the close, hide the window and keep running
        // (playback and the desktop lyrics stay alive). Real exit paths set
        // _forceExit first (tray menu) or use the "Exit" setting.
        if (!_forceExit && _settings.CloseAction == "Tray")
        {
            e.Handled = true;
            try { this.AppWindow.Hide(); }
            catch { /* best-effort */ }
            EnsureTrayIcon();
            return;
        }

        _tray?.Dispose();
        _tray = null;

        // The desktop-lyrics overlay is a separate window — close it too,
        // otherwise it keeps the process alive after the main window closes.
        try
        {
            _desktopLyrics?.Close();
        }
        catch
        {
            // best effort
        }
        _desktopLyrics = null;

        PlaylistStore.SaveProgress(_playback.CurrentIndex, _playback.Position, CurrentPath());

        // Persist window geometry + volume.
        var hwnd = WindowNative.GetWindowHandle(this);
        if (NativeMethods.GetWindowRect(hwnd, out var r))
        {
            _settings.WindowW = r.Width;
            _settings.WindowH = r.Height;
            _settings.WindowX = r.Left;
            _settings.WindowY = r.Top;
        }
        _settings.Volume = _playback.Volume;
        SettingsStore.Save(_settings);
    }

    // ---------- Helpers ----------

    private static string FormatTime(TimeSpan t)
    {
        var total = (int)t.TotalSeconds;
        if (total >= 3600)
            return $"{total / 3600}:{total / 60 % 60:D2}:{total % 60:D2}";
        return $"{total / 60:D2}:{total % 60:D2}";
    }

    private static string ToHex(Windows.UI.Color c) =>
        $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static Windows.UI.Color ParseHex(string? hex)
    {
        try
        {
            var h = (hex ?? "#FFFFFFFF").Trim().TrimStart('#');
            if (h.Length == 8)
                return Windows.UI.Color.FromArgb(
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16),
                    Convert.ToByte(h.Substring(6, 2), 16));
            if (h.Length == 6)
                return Windows.UI.Color.FromArgb(255,
                    Convert.ToByte(h.Substring(0, 2), 16),
                    Convert.ToByte(h.Substring(2, 2), 16),
                    Convert.ToByte(h.Substring(4, 2), 16));
        }
        catch
        {
            // ignore
        }

        return Windows.UI.Color.FromArgb(255, 255, 255, 255);
    }

    private void InitPicker(object picker)
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);
    }

    private async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitPicker(picker);
        picker.FileTypeFilter.Add("*");
        return await picker.PickSingleFolderAsync();
    }
}
