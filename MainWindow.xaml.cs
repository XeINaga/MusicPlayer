using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Hosting;
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
    private enum NavView { Local, Recent, Favorites, Albums, MostPlayed, Artists, Playlist, Settings, LyricFill, NowPlaying }

    private readonly PlaybackService _playback = new();
    private readonly ObservableCollection<Track> _library = new();
    private readonly ObservableCollection<Track> _recent = new();
    private readonly ObservableCollection<Playlist> _playlists = new();
    private readonly AppSettings _settings = SettingsStore.Load();

    // Guards ThemeModeCombo.SelectedIndex from re-entering its own handler.
    private bool _applyingThemeMode;
    // Set once the first theme has been applied, so a live switch can refresh the
    // brushes that were assigned from code (they don't re-resolve like ThemeResource).
    private bool _themeModeApplied;
    private readonly DispatcherQueue _dispatcher;
    private readonly BulkObservableCollection<Track> _displayTracks = new();

    private IList<Track> _activeTracks = new ObservableCollection<Track>();
    private NavView _currentView = NavView.Local;
    private Playlist? _currentPlaylist;
    private IList<Track>? _boundQueue;

    private LyricDocument? _lyrics;
    private readonly List<StackPanel> _lyricPanels = new();
    private FrameworkElement? _lyricScrollTarget;
    private int _currentLineIndex = -1;
    private int _loadedIndex = -1;

    private DesktopLyricsOverlay? _desktopLyrics;
    private readonly PlayMode[] _modeOrder = { PlayMode.Sequential, PlayMode.LoopAll, PlayMode.LoopOne, PlayMode.Random };
    private static readonly double[] _speedCycle = { 1.0, 1.25, 1.5, 2.0, 0.5, 0.75 };

    private bool _isSeeking;
    private bool _sized;
    private bool _isPlaying;
    private bool _libraryDirty;
    private DateTime _lastProgressSave = DateTime.MinValue;
    private Track? _currentTrack;
    private Track? _contextTrack;
    private string _searchText = string.Empty;

    private string _albumFilter = string.Empty;  // non-empty = show tracks from this album
    private string _artistFilter = string.Empty;  // non-empty = show tracks from this artist

    private string _viewMode = "Grid";   // "Grid" | "List"
    private string _sortBy = "Default";   // Default|Title|Artist|Album|DateAdded|Duration

    // Last.fm scrobbling.
    private readonly LastFmService _lastFm;
    private readonly List<ScrobbleEntry> _scrobbleQueue = new();
    private bool _scrobbleTimerRunning;
    private readonly DispatcherTimer _scrobbleTimer = new();
    private readonly HashSet<string> _scrobbledThisSession = new(); // dedup per session

    /// <summary>Lightweight DTO for the album grid (not a Track, just grouping metadata).</summary>
    private sealed class AlbumInfo
    {
        public required string Name { get; init; }
        public required string Artist { get; init; }
        public required int TrackCount { get; init; }
        public required IList<Track> Tracks { get; init; }
        /// <summary>Cover image of the first track (may be null).</summary>
        public ImageSource? Cover => Tracks.Count > 0 ? Tracks[0].Cover : null;
    }

    /// <summary>Lightweight DTO for the artist grid.</summary>
    private sealed class ArtistInfo
    {
        public required string ArtistName { get; init; }
        public required int TrackCount { get; init; }
        public required IList<Track> Tracks { get; init; }
        public string TrackCountText => $"{TrackCount} 首歌曲";
    }

    // Close-to-tray support.
    private TrayIconService? _tray;
    private HotkeyService? _hotkey;
    private bool _forceExit;         // real exit requested (tray menu / second close)
    private bool _trayHintShown;     // balloon only on the first hide per session

    // "Recently played" bookkeeping: the restored session queue must not count
    // as "played" until playback actually starts.
    // Which play the recent entry was already made for. Held as a Track, not an
    // index: the same number means different tracks in different queues, and
    // comparing bare indices across a queue switch silently drops the entry.
    private Track? _lastRecentTrack;
    // Guard for PlayCount: the track the last "Playing" state was counted for.
    // Deliberately separate from _lastRecentTrack (recent-list dedup) — see
    // the comment in OnStateChanged.
    private Track? _lastCountedTrack;
    private bool _suppressNextRecent;
    // Consecutive playback failures (stop skipping when the whole queue is bad).
    private int _consecutiveFailures;
    private readonly DispatcherTimer _errorBarTimer = new();

    // Multi-select mode for batch "add to playlist".
    private bool _selectMode;
    // Current track captured when a queue drag starts (to resync the index).
    private Track? _queueDragCurrent;
    // True while the vinyl's composition spin animation is running.
    private bool _spinRunning;

    private readonly DispatcherTimer _lyricTimer = new();
    private readonly DispatcherTimer _sleepTimer = new();
    private readonly DispatcherTimer _searchDebounceTimer = new();
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

        // Lyric highlight clock. The player's PositionChanged only fires a few
        // times per second (and late after long dispatcher queues), which made
        // the highlighted line lag behind the song. Polling the session
        // position directly on this fast timer keeps the highlight in sync.
        _lyricTimer.Interval = TimeSpan.FromMilliseconds(40);
        _lyricTimer.Tick += (_, _) => UpdateLyricHighlight(_playback.Position);

        _errorBarTimer.Interval = TimeSpan.FromSeconds(5);
        _errorBarTimer.Tick += (_, _) =>
        {
            _errorBarTimer.Stop();
            PlayErrorBar.IsOpen = false;
        };

        _searchDebounceTimer.Interval = TimeSpan.FromMilliseconds(300);
        _searchDebounceTimer.Tick += (_, _) =>
        {
            _searchDebounceTimer.Stop();
            _searchText = SearchBox.Text.Trim();
            RefreshDisplay();
        };

        // Restore persisted volume (so it matches the last session).
        _playback.Volume = _settings.Volume;
        VolumeSlider.Value = _settings.Volume * 100.0;
        SeekSlider.Maximum = 1;

        // Dynamic volume (loudness normalization) routing.
        _playback.LoudnessNormalization = _settings.DynamicVolume;

        // Filename-vs-tag title display + QQ login cookie for the lyric source.
        MetadataService.PreferFilenameTitles = _settings.TitlePreferFilename;
        QQLyricService.SetCookie(_settings.QqCookie);

        // Restore persisted playback rate.
        _playback.Rate = _settings.PlaybackRate;
        UpdateSpeedText();

        // Apply the persisted theme color before the first paint.
        ApplyAccentColor();

        // Switch the whole visual language (dark / light) before the first paint.
        ApplyThemeMode();

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

        // Sync auto-start: registry is the source of truth on launch.
        _settings.AutoStart = AutoStart.IsAutoStartEnabled();
        SettingsStore.Save(_settings);

        // Restore view + sort preferences.
        _viewMode = _settings.ViewMode == "List" ? "List" : "Grid";
        _sortBy = _settings.SortBy;

        // Spinning vinyl disc (animates only while playing AND spin is enabled);
        // driven by a composition animation — see UpdateDiscSpin.
        CoverDisc.SizeChanged += (_, _) =>
        {
            // Rotation must be centered on the real disc, whenever layout lands.
            var v = ElementCompositionPreview.GetElementVisual(CoverDisc);
            v.CenterPoint = new System.Numerics.Vector3(
                (float)(CoverDisc.ActualWidth / 2), (float)(CoverDisc.ActualHeight / 2), 0);
        };

        // Sleep timer: fires once after the chosen interval to pause playback.
        _sleepTimer.Tick += OnSleepTimerTick;

        // Last.fm scrobbling service.
        _lastFm = new LastFmService(_settings);
        _lastFm.StatusChanged += msg => _dispatcher.TryEnqueue(() => ShowInfoBar(msg));
        _scrobbleTimer.Interval = TimeSpan.FromSeconds(2);
        _scrobbleTimer.Tick += (_, _) => FlushScrobbleQueue();

        // Restore persisted play mode.
        if (Enum.TryParse<PlayMode>(_settings.DefaultPlayMode, out var m))
            _playback.Mode = m;
        ApplyPlayModeLabel();

        // Restore crossfade duration.
        _playback.CrossfadeDurationMs = _settings.CrossfadeDurationMs;

        UpdateLyricOffsetText();
        LyricRomajiToggle.IsChecked = _settings.LyricShowRomaji;
        LyricTransToggle.IsChecked = _settings.LyricShowTranslation;
        WireSeekSlider();
        WireVolumeIcon();

        // Slider range set in code: this exact slider's Minimum/Maximum as XAML
        // attributes produced a corrupt XBF node ("Failed to assign to property
        // 'RangeBase.Minimum'" at runtime) even though it compiled fine.
        // The range assignment coerces Value (0 -> 12) which fires
        // ValueChanged; the guard keeps that from saving 12 over the user's
        // persisted font size before it was ever read back.
        _suppressSettingEvents = true;
        LyricFontSlider.Minimum = 12;
        LyricFontSlider.Maximum = 72;
        _suppressSettingEvents = false;

        // Play button: a gentle grow on hover — it is the transport bar's
        // focal point. Scale is center-anchored; Vector3Transition animates it.
        BtnPlay.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(120) };
        BtnPlay.PointerEntered += (_, _) => BtnPlay.Scale = new System.Numerics.Vector3(1.06f, 1.06f, 1f);
        BtnPlay.PointerExited += (_, _) => BtnPlay.Scale = System.Numerics.Vector3.One;

        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;

        RestoreSession();
        ShowView(NavView.Local);

        // Watched folders: reconcile the library with what is on disk (runs
        // async; it is pure disk I/O plus dispatcher-marshaled list edits).
        SafeRun(SyncWatchedFoldersAsync, "监控文件夹同步");

        // Apply the embedded app icon to the window title bar / taskbar.
        TrySetWindowIcon();

        // Modern merged title bar: XAML content extends into the caption area.
        SetupTitleBar();

        // Global hotkeys: register if the user enabled them.
        if (_settings.UseGlobalHotkeys)
            EnableHotkeys();

        // Retry any pending Last.fm scrobbles from a previous session.
        _ = Task.Run(async () => await _lastFm.RetryFailedScrobblesAsync());
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

    // ---------- Global hotkeys ----------

    private void EnableHotkeys()
    {
        if (_hotkey != null)
            return;

        _hotkey = new HotkeyService();
        _hotkey.PlayPauseRequested += () => _dispatcher.TryEnqueue(() => _playback.PlayPause());
        _hotkey.NextRequested += () => _dispatcher.TryEnqueue(() => _playback.Next());
        _hotkey.PreviousRequested += () => _dispatcher.TryEnqueue(() => _playback.Previous());
        _hotkey.RegistrationFailed += msg => _dispatcher.TryEnqueue(() =>
            ShowInfoBar($"全局快捷键注册失败（可能被其他程序占用）：{msg}"));
        _hotkey.Register();
    }

    private void DisableHotkeys()
    {
        _hotkey?.Dispose();
        _hotkey = null;
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
        var entries = PlaylistStore.LoadAutoPlaylist();
        if (entries.Count > 0)
            AddItemsFromStore(entries);

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

                // Random mode: the in-session back history starts empty, which
                // used to make the first "previous" press jump to a random
                // track. 最近播放 IS the cross-session play history — seed the
                // stack from it (most recent 50, resumed track excluded,
                // pushed oldest-first so the previous song sits on top).
                var seed = _recent.Where(t => t.Path != prog.Path)
                                  .Take(50)
                                  .ToList();
                seed.Reverse();
                _playback.SeedRandomHistory(seed);
            }
        }
    }

    // ---------- Navigation ----------

    private void NavRecent_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Recent);
    private void NavLocal_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Local);
    private void NavFavorites_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Favorites);
    private void NavMostPlayed_Click(object sender, RoutedEventArgs e) => ShowView(NavView.MostPlayed);
    private void NavArtists_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Artists);
    private void NavAlbums_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Albums);
    private void NavSettings_Click(object sender, RoutedEventArgs e) => ShowView(NavView.Settings);
    private void NavLyricFill_Click(object sender, RoutedEventArgs e) => ShowView(NavView.LyricFill);

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
            case NavView.Favorites:
                ContentTitle.Text = "我的收藏";
                _activeTracks = _library.Where(t => t.Favorite).ToList();
                ShowActions("favorites");
                break;
            case NavView.MostPlayed:
                ContentTitle.Text = "最常播放";
                _activeTracks = _library.OrderByDescending(t => t.PlayCount).Take(50).ToList();
                ShowActions("mostplayed");
                break;
            case NavView.Artists:
                ContentTitle.Text = "歌手";
                _artistFilter = string.Empty;
                _activeTracks = _library;
                ShowActions("artists");
                BuildArtistGrid();
                break;
            case NavView.Albums:
                ContentTitle.Text = "专辑";
                _albumFilter = string.Empty;
                _activeTracks = _library;
                ShowActions("albums");
                BuildAlbumGrid();
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
            case NavView.LyricFill:
                ContentTitle.Text = "歌词补全";
                ShowActions("lyricfill");
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
        bool library = _currentView is NavView.Local or NavView.Recent or NavView.Favorites or NavView.MostPlayed or NavView.Playlist;
        bool albums = _currentView == NavView.Albums;
        bool albumDrillDown = albums && !string.IsNullOrEmpty(_albumFilter);
        bool artists = _currentView == NavView.Artists;
        bool artistDrillDown = artists && !string.IsNullOrEmpty(_artistFilter);
        bool grid = _viewMode == "Grid";

        LibraryHeader.Visibility = (library || albumDrillDown || artistDrillDown) ? Visibility.Visible : Visibility.Collapsed;

        if (_currentView == NavView.NowPlaying)
        {
            CenterCol.Width = new GridLength(0);
            RightCol.Width = new GridLength(1, GridUnitType.Star);
            CenterGrid.Visibility = Visibility.Collapsed;
            NowPlayingPanel.Visibility = Visibility.Visible;
            AnimatePanelIn(NowPlayingPanel, NowPanelTransform, fromX: 56, fromY: 0);
            TrackGrid.Visibility = Visibility.Collapsed;
            TrackList.Visibility = Visibility.Collapsed;
            AlbumGrid.Visibility = Visibility.Collapsed;
            AlbumFilterBar.Visibility = Visibility.Collapsed;
            ArtistBrowsePanel.Visibility = Visibility.Collapsed;
            SettingsScroll.Visibility = Visibility.Collapsed;
            LyricCompletionPanel.Visibility = Visibility.Collapsed;

            // While the panel was collapsed the lyric ScrollViewer had no
            // layout, so earlier auto-scrolls were discarded — re-center the
            // current line once this visibility pass has been laid out.
            _dispatcher.TryEnqueue(ScrollLyricToCurrent);
        }
        else
        {
            CenterCol.Width = new GridLength(1, GridUnitType.Star);
            RightCol.Width = new GridLength(0);
            CenterGrid.Visibility = Visibility.Visible;
            NowPlayingPanel.Visibility = Visibility.Collapsed;
            SettingsScroll.Visibility = _currentView == NavView.Settings ? Visibility.Visible : Visibility.Collapsed;
            LyricCompletionPanel.Visibility = _currentView == NavView.LyricFill ? Visibility.Visible : Visibility.Collapsed;

            if (albums && !albumDrillDown)
            {
                // Album overview: show album grid only
                TrackGrid.Visibility = Visibility.Collapsed;
                TrackList.Visibility = Visibility.Collapsed;
                AlbumGrid.Visibility = Visibility.Visible;
                AlbumFilterBar.Visibility = Visibility.Collapsed;
                ArtistBrowsePanel.Visibility = Visibility.Collapsed;
            }
            else if (albumDrillDown)
            {
                // Album drill-down: show filtered track list + back bar
                AlbumGrid.Visibility = Visibility.Collapsed;
                AlbumFilterBar.Visibility = Visibility.Visible;
                TrackGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
                TrackList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
                ArtistBrowsePanel.Visibility = Visibility.Collapsed;
            }
            else if (artists && !artistDrillDown)
            {
                // Artist overview: show artist grid only
                TrackGrid.Visibility = Visibility.Collapsed;
                TrackList.Visibility = Visibility.Collapsed;
                AlbumGrid.Visibility = Visibility.Collapsed;
                AlbumFilterBar.Visibility = Visibility.Collapsed;
                ArtistBrowsePanel.Visibility = Visibility.Visible;
                ArtistGrid.Visibility = Visibility.Visible;
                BtnArtistBack.Visibility = Visibility.Collapsed;
            }
            else if (artistDrillDown)
            {
                // Artist drill-down: show filtered track list + back button
                TrackGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
                TrackList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
                AlbumGrid.Visibility = Visibility.Collapsed;
                AlbumFilterBar.Visibility = Visibility.Collapsed;
                ArtistBrowsePanel.Visibility = Visibility.Visible;
                ArtistGrid.Visibility = Visibility.Collapsed;
                BtnArtistBack.Visibility = Visibility.Visible;
            }
            else if (library)
            {
                TrackGrid.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
                TrackList.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
                AlbumGrid.Visibility = Visibility.Collapsed;
                AlbumFilterBar.Visibility = Visibility.Collapsed;
                ArtistBrowsePanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                TrackGrid.Visibility = Visibility.Collapsed;
                TrackList.Visibility = Visibility.Collapsed;
                AlbumGrid.Visibility = Visibility.Collapsed;
                AlbumFilterBar.Visibility = Visibility.Collapsed;
                ArtistBrowsePanel.Visibility = Visibility.Collapsed;
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
            case "favorites":
                BtnSelectMode.Visibility = Visibility.Visible;
                ViewCombo.Visibility = Visibility.Visible;
                SortCombo.Visibility = Visibility.Visible;
                break;
            case "mostplayed":
                BtnSelectMode.Visibility = Visibility.Visible;
                ViewCombo.Visibility = Visibility.Visible;
                SortCombo.Visibility = Visibility.Visible;
                break;
            case "artists":
                break;
            case "lyricfill":
                // The completion page carries its own buttons; none of the
                // library toolbar actions apply here.
                break;
            case "artisttracks":
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

        // Grouped drop-downs only appear when at least one entry inside them is
        // available for the current view, so a group never shows up empty.
        SyncGroupVisibility(BtnAddMenu, BtnAddFile, BtnAddFolderRecursive, BtnAddFolderFlat, BtnOpenList);
        SyncGroupVisibility(BtnListMenu, BtnSaveList, BtnClear, BtnClearRecent);
        SyncGroupVisibility(BtnPlaylistMenu, BtnPlayPlaylist, BtnAddToPlaylist, BtnRenamePlaylist, BtnDeletePlaylist);
    }

    /// <summary>
    /// Show a grouped drop-down only when at least one of its menu entries is
    /// visible, so a group never shows up empty.
    /// </summary>
    private static void SyncGroupVisibility(UIElement group, params MenuFlyoutItem[] items)
    {
        bool anyVisible = false;
        foreach (var item in items)
        {
            if (item.Visibility == Visibility.Visible)
            {
                anyVisible = true;
                break;
            }
        }
        group.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetNavSelected(NavView view)
    {
        NavRecent.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        NavLocal.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        NavFavorites.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        NavMostPlayed.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        NavAlbums.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        NavArtists.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        NavSettings.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var sel = view switch
        {
            NavView.Recent => NavRecent,
            NavView.Favorites => NavFavorites,
            NavView.MostPlayed => NavMostPlayed,
            NavView.Albums => NavAlbums,
            NavView.Artists => NavArtists,
            NavView.Settings => NavSettings,
            NavView.LyricFill => NavLyricFill,
            NavView.NowPlaying => null,
            _ => NavLocal
        };
        if (sel != null && FindResource("NavSelected") is SolidColorBrush nav)
            sel.Background = nav;
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

    private void BtnAddFile_Click(object sender, RoutedEventArgs e)
        => SafeRun(BtnAddFileAsync, "添加文件");

    private async Task BtnAddFileAsync()
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

    private void BtnAddFolderRecursive_Click(object sender, RoutedEventArgs e)
        => SafeRun(BtnAddFolderRecursiveAsync, "添加文件夹");

    private async Task BtnAddFolderRecursiveAsync()
    {
        var folder = await PickFolderAsync();
        if (folder == null)
            return;
        // Recursive enumeration walks up to 5000 files and 8 levels deep. That
        // is seconds of blocking I/O on a spinning disk or a USB drive, which
        // would freeze the window if it ran on the UI thread.
        var paths = await Task.Run(() => FolderScanner.Scan(folder.Path, recursive: true));
        AddItemsToLibrary(paths);
        WatchFolder(folder.Path, recursive: true);
    }

    private void BtnAddFolderFlat_Click(object sender, RoutedEventArgs e)
        => SafeRun(BtnAddFolderFlatAsync, "添加文件夹");

    private async Task BtnAddFolderFlatAsync()
    {
        var folder = await PickFolderAsync();
        if (folder == null)
            return;
        // Same reasoning as the recursive variant: keep the disk walk off the
        // UI thread.
        var paths = await Task.Run(() => FolderScanner.Scan(folder.Path, recursive: false));
        AddItemsToLibrary(paths);
        WatchFolder(folder.Path, recursive: false);
    }

    /// <summary>
    /// Register a folder for surveillance: every launch rescans it and syncs
    /// the changes (new files in, deleted files out) into the library.
    /// Re-adding the same folder just updates its recursive flag.
    /// </summary>
    private void WatchFolder(string path, bool recursive)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var existing = _settings.WatchedFolders.FirstOrDefault(w =>
            !string.IsNullOrWhiteSpace(w.Path) &&
            string.Equals(
                Path.GetFullPath(w.Path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                normalized, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Recursive = recursive;
        }
        else
        {
            _settings.WatchedFolders.Add(new WatchedFolder { Path = path, Recursive = recursive });
        }
        SettingsStore.Save(_settings);
        ShowInfoBar($"已监控文件夹（每次启动自动同步变更）：{path}");
    }

    private void BtnOpenList_Click(object sender, RoutedEventArgs e)
        => SafeRun(BtnOpenListAsync, "导入列表");

    private async Task BtnOpenListAsync()
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

    private void BtnSaveList_Click(object sender, RoutedEventArgs e)
        => SafeRun(BtnSaveListAsync, "导出列表");

    private async Task BtnSaveListAsync()
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
        // Scanning the library once per path is O(n^2): importing a folder into
        // a 5k-track library costs ~12M string comparisons. Windows paths are
        // case-insensitive, so Ordinal also catches casing variants of a file
        // that is already there.
        var seen = new HashSet<string>(_library.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (!seen.Add(p))
                continue;
            // A manual add is a deliberate act: clear any watched-folder
            // exclusion so the startup sync will not fight the user over it.
            _settings.LibraryExclusions.Remove(p);
            var track = new Track(p);
            track.LyricPath = LyricBindingStore.Get(p);
            _library.Add(track);
            LoadMetadataFor(track);
            added = true;
        }

        if (added && _settings.LibraryExclusions.Count > 0 && persist)
            SettingsStore.Save(_settings);

        if (persist)
            PersistLibrary();

        if (added && _playback.Queue == null && _library.Count > 0)
        {
            _activeTracks = _library;
            LoadLyricsFor(0);
        }

        RefreshDisplay();
    }

    /// <summary>
    /// Restore library tracks from persisted store, preserving DateAdded,
    /// PlayCount, and Favorite from the v2 serialization format.
    /// </summary>
    private void AddItemsFromStore(List<TrackEntry> entries)
    {
        if (entries.Count == 0)
            return;

        var added = false;
        // Same O(n^2) trap as AddItemsToLibrary, but on the restore path — it
        // runs on every startup against the full persisted library.
        var seen = new HashSet<string>(_library.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            if (!seen.Add(e.Path))
                continue;
            var track = new Track(e.Path)
            {
                DateAdded = e.DateAdded,
                PlayCount = e.PlayCount,
                Favorite = e.Favorite
            };
            track.LyricPath = LyricBindingStore.Get(e.Path);
            _library.Add(track);
            LoadMetadataFor(track);
            added = true;
        }

        if (added && _playback.Queue == null && _library.Count > 0)
        {
            _activeTracks = _library;
            LoadLyricsFor(0);
        }

        RefreshDisplay();
    }

    // Caps how many tag reads run at once. Restoring a large library used to
    // start one per track immediately — 5k tracks meant 5k queued Task.Runs
    // followed by 5k separate UI callbacks, which pinned the UI for as long as
    // it took to drain.
    private readonly SemaphoreSlim _metadataGate = new(8);

    private async void LoadMetadataFor(Track track)
    {
        await _metadataGate.WaitAsync();
        try
        {
            await MetadataService.LoadAsync(track, _dispatcher);
        }
        catch
        {
            // best-effort: a failed tag read must not tear down the app
        }
        finally
        {
            _metadataGate.Release();
        }
    }

    private void PersistLibrary() =>
        PlaylistStore.SaveAutoPlaylist(_library);

    // Chains background library saves; see PersistLibraryBackground.
    private Task _librarySaveTask = Task.CompletedTask;

    /// <summary>
    /// Persist the library without blocking the UI. A few thousand tracks turn
    /// into a few hundred KB of JSON plus three file operations, which is enough
    /// to stutter playback when it runs on the position tick. Use this for
    /// periodic saves; callers that must not lose the write (app exit) should
    /// keep using <see cref="PersistLibrary"/>.
    /// </summary>
    private void PersistLibraryBackground()
    {
        // Snapshot on the UI thread — the collection is only touched here.
        var snapshot = _library.ToList();
        // Chained, so a slow save cannot land after a newer one and let a stale
        // snapshot overwrite fresh data.
        _librarySaveTask = _librarySaveTask.ContinueWith(_ =>
        {
            try
            {
                PlaylistStore.SaveAutoPlaylist(snapshot);
            }
            catch
            {
                // best-effort: a failed background save must not take down the app
            }
        }, TaskScheduler.Default);
    }

    private void PersistRecent() =>
        PlaylistStore.SaveRecent(_recent);

    // ---------- Watched-folder startup sync ----------

    /// <summary>
    /// Rescan every watched folder and reconcile the library with what is on
    /// disk: audio files that appeared are added, files that vanished are
    /// removed. Runs once per launch, right after the session restore.
    /// </summary>
    private async Task SyncWatchedFoldersAsync()
    {
        var watched = _settings.WatchedFolders
            .Where(w => !string.IsNullOrWhiteSpace(w.Path))
            .Where(w => Directory.Exists(w.Path))
            .ToList();
        if (watched.Count == 0)
            return;

        // Disk walk off the UI thread; the reconciliation below needs the
        // library collection, so it stays on the dispatcher.
        var scanned = await Task.Run(() => watched
            .Select(w => (Root: w, Files: FolderScanner.Scan(w.Path, w.Recursive)))
            .ToList());

        var union = new HashSet<string>(
            scanned.SelectMany(s => s.Files), StringComparer.OrdinalIgnoreCase);

        // Files to add: in a watched folder, not already in the library, and
        // not previously removed by the user on purpose.
        var known = new HashSet<string>(_library.Select(t => t.Path), StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(_settings.LibraryExclusions, StringComparer.OrdinalIgnoreCase);
        var toAdd = union.Where(p => !known.Contains(p) && !excluded.Contains(p)).ToList();

        // Tracks to drop: live under a watched root but no longer on disk.
        var toRemove = new List<int>();
        for (var i = 0; i < _library.Count; i++)
        {
            var p = _library[i].Path;
            if (!union.Contains(p) && watched.Any(w => UnderRoot(p, w.Path)))
                toRemove.Add(i);
        }

        if (toAdd.Count == 0 && toRemove.Count == 0)
            return;

        if (toRemove.Count > 0)
        {
            // Drop from the end downwards so the pending indices stay valid.
            // NOT SyncAfterRemoval here: it answers a removed playing track by
            // loading the next one (play: true), which would auto-start
            // playback at launch. The restored session is paused; re-point it
            // silently or drop it, but never start playing.
            var curPath = CurrentPath();
            foreach (var i in toRemove.AsEnumerable().Reverse())
                _library.RemoveAt(i);

            if (ReferenceEquals(_playback.Queue, _library))
            {
                var idx = -1;
                if (curPath != null)
                    idx = _library.ToList().FindIndex(t => t.Path == curPath);

                if (idx >= 0)
                {
                    _playback.SetIndexSilent(idx);
                    QueueList.SelectedIndex = idx;
                }
                else
                {
                    // The track the session wanted to resume is gone — there is
                    // nothing meaningful to point at, so retire the queue
                    // instead of silently playing a neighbour.
                    _playback.Clear();
                    ResetNowPlaying();
                    QueueList.SelectedIndex = -1;
                }
            }
        }

        if (toAdd.Count > 0)
            AddItemsToLibrary(toAdd, persist: false);

        PersistLibrary();
        ShowInfoBar($"文件夹监控同步完成：新增 {toAdd.Count}，移除 {toRemove.Count}");
        AppLog.WriteLyricCompletion($"监控文件夹同步：新增 {toAdd.Count}，移除 {toRemove.Count}");
    }

    /// <summary>Is <paramref name="path"/> inside <paramref name="root"/> (or the root itself)?</summary>
    private static bool UnderRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return false;
        try
        {
            var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            return path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

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
        // Clearing while the files still sit inside watched folders must not
        // be undone by the next startup sync — exclude them, same as a normal
        // remove-from-library does.
        foreach (var t in _library)
            if (!_settings.LibraryExclusions.Contains(t.Path, StringComparer.OrdinalIgnoreCase))
                _settings.LibraryExclusions.Add(t.Path);
        if (_settings.LibraryExclusions.Count > 0)
            SettingsStore.Save(_settings);

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
        foreach (var entry in PlaylistStore.LoadRecent())
        {
            var t = ResolveTrack(entry.Path);
            if (t != null)
            {
                // Apply persisted DateAdded/PlayCount/Favorite from store.
                t.DateAdded = entry.DateAdded;
                t.PlayCount = entry.PlayCount;
                t.Favorite = entry.Favorite;
                if (!_recent.Contains(t))
                    _recent.Add(t);
            }
        }
    }

    private void LoadPlaylistsFromStore()
    {
        // ResolveTrack() scans the library linearly, and this loop calls it once
        // per path per playlist: O(playlists x tracks x library). Build the
        // lookup once and keep it in sync with the tracks we add.
        var byPath = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _library)
            byPath[t.Path] = t;

        foreach (var dto in PlaylistStore.LoadPlaylists())
        {
            var pl = new Playlist { Name = dto.Name };
            if (dto.Paths != null)
            {
                foreach (var p in dto.Paths)
                {
                    if (!byPath.TryGetValue(p, out var t))
                    {
                        t = new Track(p);
                        t.LyricPath = LyricBindingStore.Get(p);
                        _library.Add(t);
                        byPath[p] = t;
                        LoadMetadataFor(t);
                    }
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

    private void BtnNewPlaylist_Click(object sender, RoutedEventArgs e)
        => SafeRun(() => BtnNewPlaylistAsync(sender), "新建歌单");

    private async Task BtnNewPlaylistAsync(object sender)
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

    private void BtnAddToPlaylist_Click(object sender, RoutedEventArgs e)
        => SafeRun(() => BtnAddToPlaylistAsync(sender), "添加歌曲");

    private async Task BtnAddToPlaylistAsync(object sender)
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

    private void BtnDeletePlaylist_Click(object sender, RoutedEventArgs e)
        => SafeRun(() => BtnDeletePlaylistAsync(sender), "删除歌单");

    private async Task BtnDeletePlaylistAsync(object sender)
    {
        if (_currentPlaylist == null)
            return;
        await DeletePlaylistConfirmed(_currentPlaylist);
    }

    private void BtnRenamePlaylist_Click(object sender, RoutedEventArgs e)
        => SafeRun(() => BtnRenamePlaylistAsync(sender), "重命名歌单");

    private async Task BtnRenamePlaylistAsync(object sender)
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

    /// <summary>
    /// Keep the player pointing at the same track after <paramref name="list"/>
    /// has just lost the tracks at <paramref name="removed"/>. Does nothing when
    /// the player is playing some other collection, or when the removal did not
    /// touch the position it is on.
    /// </summary>
    private void SyncAfterRemoval(IList<Track> list, IEnumerable<int> removed)
    {
        if (!ReferenceEquals(_playback.Queue, list))
            return;

        var indices = removed.ToList();
        if (indices.Count == 0)
            return;

        var current = _playback.CurrentIndex;
        if (current < 0)
            return;

        if (indices.Contains(current))
        {
            // The playing track itself went away: take whatever slid into its
            // slot, or stop if nothing is left.
            if (list.Count > 0)
                _playback.TakeOverAfterRemoval(Math.Min(current, list.Count - 1));
            else
            {
                _playback.Clear();
                ResetNowPlaying();
            }
            return;
        }

        // Later indices shifted down past the current one.
        var shift = indices.Count(i => i < current);
        if (shift > 0)
            _playback.ShiftIndex(-shift);
    }

    private void RemoveFromCurrentPlaylist(Track t)
    {
        if (_currentPlaylist == null)
            return;

        var idx = _currentPlaylist.Tracks.IndexOf(t);
        if (idx < 0)
            return;

        _currentPlaylist.Tracks.RemoveAt(idx);
        // 播放全部 hands the playlist itself to SetQueue, so the queue may well
        // be this collection — in which case every later index just shifted.
        SyncAfterRemoval(_currentPlaylist.Tracks, new[] { idx });

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
        BtnBatchRemove.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;
        BtnBatchExport.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;
        BtnSelectAll.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;
        BtnSelectNone.Visibility = _selectMode ? Visibility.Visible : Visibility.Collapsed;

        SyncGroupVisibility(BtnSelectMenu, BtnSelectAll, BtnSelectNone, BtnBatchAdd, BtnBatchRemove, BtnBatchExport);
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

    private void BtnBatchRemove_Click(object sender, RoutedEventArgs e)
    {
        var sel = (_viewMode == "Grid" ? TrackGrid.SelectedItems : TrackList.SelectedItems)
            .Cast<Track>().ToList();
        if (sel.Count == 0)
            return;

        if (_currentView == NavView.Playlist && _currentPlaylist != null)
        {
            // Same shape as the library branch below: a playlist can be the
            // playback queue, so the player has to be told about the removals.
            var tracks = _currentPlaylist.Tracks;
            var removed = sel.Select(t => tracks.IndexOf(t))
                             .Where(i => i >= 0)
                             .OrderByDescending(i => i)
                             .ToList();

            foreach (var i in removed)
                tracks.RemoveAt(i);
            SyncAfterRemoval(tracks, removed);
            PersistPlaylists();
        }
        else
        {
            // Playing from 本地音乐 hands _library itself to SetQueue, so the
            // playback queue *is* this collection: removing a track shifts every
            // later index, and the player would silently keep its old index and
            // point at a different track. Collect indices first and drop them
            // from the end downwards so the pending ones stay valid.
            var indices = sel.Select(t => _library.IndexOf(t))
                             .Where(i => i >= 0)
                             .OrderByDescending(i => i)
                             .ToList();

            foreach (var i in indices)
                _library.RemoveAt(i);
            PersistLibrary();

            // The files still exist inside watched folders — remember the
            // removal so the startup sync does not quietly re-add them.
            foreach (var t in sel)
                if (!_settings.LibraryExclusions.Contains(t.Path, StringComparer.OrdinalIgnoreCase))
                    _settings.LibraryExclusions.Add(t.Path);
            SettingsStore.Save(_settings);

            SyncAfterRemoval(_library, indices);
        }

        // Also clear from recent if the tracks were removed from the library.
        if (_currentView != NavView.Playlist)
        {
            // 最近播放 can be the queue too, so it needs the same treatment.
            var removed = sel.Select(t => _recent.IndexOf(t))
                             .Where(i => i >= 0)
                             .OrderByDescending(i => i)
                             .ToList();

            foreach (var i in removed)
                _recent.RemoveAt(i);
            SyncAfterRemoval(_recent, removed);
            PersistRecent();
        }

        BtnSelectMode.IsChecked = false;
        RefreshDisplay();
    }

    private void BtnBatchExport_Click(object sender, RoutedEventArgs e)
        => SafeRun(BtnBatchExportAsync, "导出选中");

    private async Task BtnBatchExportAsync()
    {
        var sel = (_viewMode == "Grid" ? TrackGrid.SelectedItems : TrackList.SelectedItems)
            .Cast<Track>().ToList();
        if (sel.Count == 0)
            return;

        var picker = new FileSavePicker();
        InitPicker(picker);
        picker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
        picker.FileTypeChoices.Add("播放列表", new[] { ".m3u" });
        picker.SuggestedFileName = "选中歌曲";
        var file = await picker.PickSaveFileAsync();
        if (file != null)
            PlaylistStore.ExportM3U(file.Path, sel);
    }

    private void BtnSelectAll_Click(object sender, RoutedEventArgs e)
    {
        // Both lists share the same Track items; SelectAll on the hidden one
        // keeps selection consistent when the user swaps card/list view.
        TrackGrid.SelectAll();
        TrackList.SelectAll();
    }

    private void BtnSelectNone_Click(object sender, RoutedEventArgs e)
    {
        // Clear() must happen while SelectionMode still ACCEPTS selection —
        // once it is None the collection disconnects and Clear throws
        // COMException 0x8000FFFF (see ApplySelectionMode).
        try { TrackGrid.SelectedItems.Clear(); } catch { }
        try { TrackList.SelectedItems.Clear(); } catch { }
    }

    // ---------- Drag & drop (onto the content grid) ----------

    private void TrackGrid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        if (e.DragUIOverride != null)
            e.DragUIOverride.Caption = "添加文件或文件夹";
    }

    private void TrackGrid_Drop(object sender, DragEventArgs e)
        => SafeRun(() => TrackGridDropAsync(sender, e), "添加拖放的文件");

    private async Task TrackGridDropAsync(object sender, DragEventArgs e)
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
                // Dropping a folder walks the whole tree; keep it off the UI
                // thread like the pick-a-folder paths do. The captured path is
                // all we need afterwards, so awaiting here is safe.
                var scanned = await Task.Run(() => FolderScanner.Scan(folder.Path, recursive: true));
                paths.AddRange(scanned);
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
            StartPlayFromView(t);
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
            StartPlayFromView(track);
    }

    private void TrackList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_selectMode)
            return;
        var dep = e.OriginalSource as DependencyObject;
        while (dep != null && !(dep is ListViewItem))
            dep = VisualTreeHelper.GetParent(dep);

        if (dep is ListViewItem item && TrackList.ItemFromContainer(item) is Track track)
            StartPlayFromView(track);
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

        if ((_currentView is NavView.Local or NavView.Recent or NavView.Favorites or NavView.MostPlayed or NavView.Artists) && _playlists.Count > 0)
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

    /// <summary>Open Explorer at (with) the track's file, so the user can jump
    /// straight to the audio / its companion lyric files.</summary>
    private void OpenContainingFolder_Click(object sender, RoutedEventArgs e)
    {
        var track = _contextTrack ?? (sender as FrameworkElement)?.DataContext as Track;
        if (track == null || string.IsNullOrEmpty(track.Path) || !File.Exists(track.Path))
        {
            ShowInfoBar("文件不存在或已被移动。");
            return;
        }

        // /select reveals the file itself; explorer still works if the file
        // vanished between the check above and the launch (it just opens the
        // folder), so no exception handling theater here.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{track.Path}\"")
        {
            UseShellExecute = true
        });
    }

    private void AssignLyric_Click(object sender, RoutedEventArgs e)
        => SafeRun(() => AssignLyricAsync(sender), "指定歌词");

    private async Task AssignLyricAsync(object sender)
    {
        var track = _contextTrack ?? (sender as FrameworkElement)?.DataContext as Track;
        if (track == null)
            return;
        await AssignLyricToTrackAsync(track);
    }

    /// <summary>
    /// Pick a local lyric file and bind it to <paramref name="track"/>.
    /// Returns true when a file was picked and bound, false when cancelled.
    /// </summary>
    private async Task<bool> AssignLyricToTrackAsync(Track track)
    {
        var picker = new FileOpenPicker();
        InitPicker(picker);
        picker.ViewMode = PickerViewMode.List;
        picker.FileTypeFilter.Add(".lrc");
        picker.FileTypeFilter.Add(".srt");
        picker.FileTypeFilter.Add(".txt");

        var file = await picker.PickSingleFileAsync();
        if (file == null)
            return false;

        track.LyricPath = file.Path;
        LyricBindingStore.Set(track.Path, file.Path);

        if (_currentTrack == track)
            LoadLyricsFor(_loadedIndex);
        return true;
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
    /// Toggle the Favorite flag on a track and persist immediately.
    /// If currently viewing Favorites, refresh so un-favorited tracks disappear.
    /// </summary>
    private void FavoriteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not Track t)
            return;

        t.Favorite = !t.Favorite;
        _libraryDirty = true;
        PersistLibrary();

        // Update the icon on the clicked button immediately.
        if (fe is Button btn && btn.Content is FontIcon icon)
            UpdateFavIcon(icon, t.Favorite);

        // If we're in the Favorites view, re-filter so the toggled track
        // appears/disappears immediately.
        if (_currentView == NavView.Favorites)
        {
            _activeTracks = _library.Where(tr => tr.Favorite).ToList();
            RefreshDisplay();
        }
    }

    /// <summary>
    /// Set the favorite icon glyph and color when a FontIcon is loaded
    /// inside a track DataTemplate.
    /// </summary>
    private void FavIcon_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FontIcon icon && icon.DataContext is Track t)
            UpdateFavIcon(icon, t.Favorite);
    }

    private void UpdateFavIcon(FrameworkElement el, bool favorite)
    {
        if (el is not FontIcon icon) return;
        icon.Glyph = favorite ? "\uE735" : "\uE734";
        if (FindResource(favorite ? "QqGreen" : "TextSecondary") is Microsoft.UI.Xaml.Media.Brush tint)
            icon.Foreground = tint;
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
        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void RefreshDisplay()
    {
        if (_activeTracks == null)
            return;

        // When an album filter is active (album drill-down), restrict to that album.
        var source = _activeTracks;
        if (_currentView == NavView.Albums && !string.IsNullOrEmpty(_albumFilter))
        {
            source = _activeTracks.Where(t =>
                string.Equals(t.Album, _albumFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var q = _searchText;
        var filtered = source.Where(t =>
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

        // One Reset instead of a Clear() followed by N Inserts — see
        // BulkObservableCollection. Search and sort rebuild this on every
        // keystroke, so the difference is noticeable on a large library.
        _displayTracks.ReplaceAll(filtered);

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

        var tracks = _currentPlaylist.Tracks;

        // Still the pre-drag order here, so this is the track actually playing —
        // provided the queue is this playlist at all.
        var playing = ReferenceEquals(_playback.Queue, tracks) &&
                      _playback.CurrentIndex >= 0 &&
                      _playback.CurrentIndex < tracks.Count
            ? tracks[_playback.CurrentIndex]
            : null;

        tracks.Clear();
        foreach (var t in _displayTracks)
            tracks.Add(t);
        PersistPlaylists();

        // The rebuild ends with the same count it started with, so the service
        // cannot detect it by length: re-point it, or Next, Previous and the
        // shuffle bag all keep working from the old position.
        if (playing == null)
            return;

        var idx = tracks.IndexOf(playing);
        if (idx >= 0)
            _playback.SetIndexSilent(idx);
    }

    private void UpdateEmptyHint()
    {
        if (_currentView == NavView.Settings)
        {
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        if (_currentView == NavView.Albums && string.IsNullOrEmpty(_albumFilter))
        {
            // Album overview: hide the track-level empty hint (album grid has its own state).
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        if (_currentView == NavView.Artists && string.IsNullOrEmpty(_artistFilter))
        {
            // Artist overview: hide the track-level empty hint (artist grid has its own state).
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

    // ---------- Album grid ----------

    /// <summary>
    /// Build the album grid: group library tracks by Album name, create one
    /// <see cref="AlbumInfo"/> per group, and bind to <see cref="AlbumGrid"/>.
    /// </summary>
    private void BuildAlbumGrid()
    {
        var albums = _library
            .Where(t => !string.IsNullOrWhiteSpace(t.Album))
            .GroupBy(t => t.Album!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new AlbumInfo
            {
                Name = g.Key,
                Artist = g.Select(t => t.Artist ?? "未知歌手").Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault() ?? "未知歌手",
                TrackCount = g.Count(),
                Tracks = g.ToList()
            })
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        AlbumGrid.ItemsSource = albums;
    }

    /// <summary>
    /// Build the artist grid: group library tracks by Artist name, create one
    /// <see cref="ArtistInfo"/> per group, and bind to <see cref="ArtistGrid"/>.
    /// </summary>
    private void BuildArtistGrid()
    {
        var artists = _library
            .Where(t => !string.IsNullOrWhiteSpace(t.Artist))
            .GroupBy(t => t.Artist!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArtistInfo
            {
                ArtistName = g.Key,
                TrackCount = g.Count(),
                Tracks = g.ToList()
            })
            .OrderBy(a => a.ArtistName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ArtistGrid.ItemsSource = artists;
    }

    private void AlbumCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlbumInfo album })
        {
            _albumFilter = album.Name;
            AlbumFilterLabel.Text = $"{album.Name}  ·  {album.TrackCount} 首";
            RefreshDisplay();
            UpdateViewVisibility();
            SearchBox.Text = string.Empty;
            _searchText = string.Empty;
        }
    }

    private void BtnAlbumBack_Click(object sender, RoutedEventArgs e)
    {
        _albumFilter = string.Empty;
        AlbumGrid.ItemsSource = null; // rebuild on next Albums view
        RefreshDisplay();
        UpdateViewVisibility();
    }

    private void AlbumCard_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            fe.Opacity = 0.88;
    }

    private void AlbumCard_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            fe.Opacity = 1.0;
    }

    private void BtnArtistBack_Click(object sender, RoutedEventArgs e)
    {
        _artistFilter = string.Empty;
        _activeTracks = _library;
        BuildArtistGrid();
        RefreshDisplay();
        UpdateViewVisibility();
    }

    private void ArtistGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ArtistInfo artist)
        {
            _artistFilter = artist.ArtistName;
            _activeTracks = artist.Tracks;
            ContentTitle.Text = artist.ArtistName;
            ShowActions("artisttracks");
            RefreshDisplay();
            UpdateViewVisibility();
            SearchBox.Text = string.Empty;
            _searchText = string.Empty;
        }
    }

    // ---------- Playing from a list ----------

    private void StartPlay(IList<Track> list, int index)
    {
        _activeTracks = list;
        _playback.SetQueue(list, index);
        BindQueue();
    }

    /// <summary>
    /// Play <paramref name="t"/> following the order currently on screen. The
    /// visible rows are a filtered / sorted projection of _activeTracks, so
    /// going through _activeTracks can walk a different order — and with a
    /// search active, queue up rows the user cannot even see.
    /// </summary>
    private void StartPlayFromView(Track t)
    {
        var view = _displayTracks.IndexOf(t);
        if (view >= 0 && ViewIsReordered())
        {
            // Snapshot: _displayTracks is rebuilt on every keystroke and sort
            // change, so the live collection would renumber the queue under the
            // player. _activeTracks is deliberately left alone so the view keeps
            // its own source.
            _playback.SetQueue(_displayTracks.ToList(), view);
            BindQueue();
            return;
        }

        var idx = _activeTracks.IndexOf(t);
        if (idx >= 0)
            StartPlay(_activeTracks, idx);
    }

    /// <summary>
    /// True when the visible list is not just <see cref="_activeTracks"/> — a
    /// search, an album drill-down or a sort is in play.
    /// </summary>
    private bool ViewIsReordered()
    {
        if (_displayTracks.Count != _activeTracks.Count)
            return true;

        for (var i = 0; i < _displayTracks.Count; i++)
        {
            if (!ReferenceEquals(_displayTracks[i], _activeTracks[i]))
                return true;
        }
        return false;
    }

    private void BindQueue()
    {
        if (_playback.Queue != _boundQueue)
        {
            _boundQueue = _playback.Queue;
            QueueList.ItemsSource = _playback.Queue;
        }

        QueueList.SelectedIndex = _playback.CurrentIndex;
        ScrollQueueToCurrent();
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

    // Volume icon scroll-to-adjust: same AddHandler pattern as WireSeekSlider
    // so the event fires even if the icon marks it handled.
    private void WireVolumeIcon()
    {
        VolumeIconArea.AddHandler(UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(VolumeIcon_PointerWheelChanged), true);
    }

    private void VolumeIcon_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(VolumeIconArea).Properties.MouseWheelDelta;
        var step = delta > 0 ? 5.0 : -5.0;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + step, 0, 100);
        e.Handled = true;
    }

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

    private void BtnSpeed_Click(object sender, RoutedEventArgs e)
    {
        var cur = _playback.Rate;
        // Find next in cycle; if current not in array, start from 0.
        var idx = Array.FindIndex(_speedCycle, s => Math.Abs(s - cur) < 0.01f);
        _playback.Rate = _speedCycle[(idx + 1) % _speedCycle.Length];
        _settings.PlaybackRate = _playback.Rate;
        SettingsStore.Save(_settings);
        UpdateSpeedText();
    }

    private void UpdateSpeedText()
    {
        if (SpeedText != null)
            SpeedText.Text = $"{_playback.Rate:0.##}x";
    }

    private void MainWindow_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (ctrl && e.Key == Windows.System.VirtualKey.A)
        {
            if (!_selectMode
                || _currentView is not (NavView.Local or NavView.Recent or NavView.Favorites or NavView.MostPlayed or NavView.Playlist))
                return;

            // Let the text box keep Ctrl+A for its own select-all.
            if (FocusManager.GetFocusedElement() is TextBox)
                return;

            BtnSelectAll_Click(sender, e);
            e.Handled = true;
            return;
        }

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

    private void BtnQueueToggle_Click(object sender, RoutedEventArgs e)
    {
        if (QueuePanel.Visibility != Visibility.Visible)
            ShowQueuePanel();
        else
            HideQueuePanel();
    }

    private void BtnQueueClose_Click(object sender, RoutedEventArgs e) =>
        HideQueuePanel();

    private void QueueDismissLayer_Tapped(object sender, TappedRoutedEventArgs e) =>
        HideQueuePanel();

    private void ShowQueuePanel()
    {
        QueueDismissLayer.Visibility = Visibility.Visible;
        QueuePanel.Visibility = Visibility.Visible;
        AnimatePanelIn(QueuePanel, QueuePanelTransform, fromX: 0, fromY: 48);
        // Let the panel run one layout pass first so the scroll target has
        // realizable items to work with.
        _dispatcher.TryEnqueue(ScrollQueueToCurrent);
    }

    private void HideQueuePanel()
    {
        if (QueuePanel.Visibility != Visibility.Visible)
            return;
        QueueDismissLayer.Visibility = Visibility.Collapsed;

        // Short fade-out, then collapse on completion.
        var fade = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(90)) };
        Storyboard.SetTarget(fade, QueuePanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            QueuePanel.Visibility = Visibility.Collapsed;
            QueuePanel.Opacity = 1;
        };
        sb.Begin();
    }

    /// <summary>
    /// Entrance animation for the floating panels: fade in while easing the
    /// render transform back to rest. Plain storyboards on a CompositeTransform
    /// (rather than composition Offset animations) so window resizes / layout
    /// moves keep working once the animation completes.
    /// </summary>
    private static void AnimatePanelIn(FrameworkElement panel, CompositeTransform transform, double fromX, double fromY)
    {
        transform.TranslateX = fromX;
        transform.TranslateY = fromY;

        var sb = new Storyboard();

        var slideX = new DoubleAnimation
        {
            From = fromX,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(170)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slideX, transform);
        Storyboard.SetTargetProperty(slideX, "TranslateX");
        sb.Children.Add(slideX);

        if (fromY != 0)
        {
            var slideY = new DoubleAnimation
            {
                From = fromY,
                To = 0,
                Duration = new Duration(TimeSpan.FromMilliseconds(170)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(slideY, transform);
            Storyboard.SetTargetProperty(slideY, "TranslateY");
            sb.Children.Add(slideY);
        }

        var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
        Storyboard.SetTarget(fadeIn, panel);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        sb.Children.Add(fadeIn);

        sb.Begin();
    }

    private void BtnQueueClear_Click(object sender, RoutedEventArgs e)
    {
        _playback.Clear();
        _boundQueue = null;
        QueueList.ItemsSource = null;
        QueueList.SelectedIndex = -1;
        ResetNowPlaying();
        LoadLyricsFor(-1);
    }

    /// <summary>Keep the queue popup following the currently playing track.
    /// Realizes the row with a quick ScrollIntoView, then glides the viewport
    /// so the row sits centered — same feel as the lyric auto-scroll.
    /// Right after the popup becomes visible the virtualizing panel has not
    /// realized any containers yet, so realization retries once per frame.</summary>
    private void ScrollQueueToCurrent() => ScrollQueueToCurrent(retries: 8);

    private void ScrollQueueToCurrent(int retries)
    {
        if (QueuePanel.Visibility != Visibility.Visible)
            return;

        var q = _playback.Queue;
        var i = _playback.CurrentIndex;
        if (q == null || i < 0 || i >= q.Count)
            return;

        QueueList.ScrollIntoView(q[i]);
        QueueList.UpdateLayout();

        if (QueueList.ContainerFromIndex(i) is not FrameworkElement container || container.ActualHeight <= 0)
        {
            if (retries > 0)
                _dispatcher.TryEnqueue(() => ScrollQueueToCurrent(retries - 1));
            return;
        }

        var sv = FindScrollViewerDescendant(QueueList);
        if (sv == null || sv.ActualHeight <= 0)
            return;

        var top = container.ActualOffset.Y - sv.ActualHeight / 2 + container.ActualHeight / 2;
        if (top > 0)
            sv.ChangeView(null, top, null); // animated by default
    }

    private static ScrollViewer? FindScrollViewerDescendant(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
                return sv;
            var inner = FindScrollViewerDescendant(child);
            if (inner != null)
                return inner;
        }
        return null;
    }

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

    /// <summary>
    /// Starts or stops the disc rotation timer based on the current playback
    /// state and the CoverSpin setting. Only spins while Playing AND spin enabled.
    /// </summary>
    /// <summary>
    /// Starts or stops the vinyl spin based on the current playback state and
    /// the CoverSpin setting. Only spins while Playing AND spin enabled. Runs
    /// as a composition keyframe animation on the element visual: real 60fps
    /// linear rotation, unlike the old DispatcherTimer stepping 0.9° per 40ms.
    /// </summary>
    private void UpdateDiscTimer()
    {
        var visual = ElementCompositionPreview.GetElementVisual(CoverDisc);
        if (_isPlaying && _settings.CoverSpin)
        {
            visual.CenterPoint = new System.Numerics.Vector3(
                (float)(CoverDisc.ActualWidth / 2), (float)(CoverDisc.ActualHeight / 2), 0);
            if (_spinRunning)
                return;
            var spin = visual.Compositor.CreateScalarKeyFrameAnimation();
            spin.InsertKeyFrame(1f, 360f);
            spin.Duration = TimeSpan.FromSeconds(16); // 22.5°/s, same speed as before
            spin.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
            visual.StartAnimation("RotationAngle", spin);
            _spinRunning = true;
        }
        else
        {
            if (!_spinRunning)
                return;
            visual.StopAnimation("RotationAngle");
            visual.RotationAngle = 0;
            _spinRunning = false;
        }
    }

    private void OnStateChanged(MediaPlaybackState state)
    {
        _isPlaying = state == MediaPlaybackState.Playing;
        BtnPlay.Content = new FontIcon { Glyph = _isPlaying ? "\uE103" : "\uE102", FontSize = 18 };
        UpdateDiscTimer();

        if (_isPlaying)
            _lyricTimer.Start();
        else
            _lyricTimer.Stop();

        if (state == MediaPlaybackState.Playing)
        {
            _consecutiveFailures = 0;

            var idx = _playback.CurrentIndex;
            var q = _playback.Queue;
            if (q != null && idx >= 0 && idx < q.Count)
            {
                var t = q[idx];

                // Count a play exactly once per continuous playing session of
                // this track: pause/resume must not re-count, switching tracks
                // must. This guard is separate from _lastRecentTrack (which
                // only dedups the 最近播放 list) — OnCurrentIndexChanged
                // pre-fills _lastRecentTrack the moment a track is selected,
                // BEFORE Playing fires, so reusing it here silently swallowed
                // every in-session play count.
                if (!ReferenceEquals(t, _lastCountedTrack))
                {
                    _lastCountedTrack = t;
                    t.LastPlayed = DateTime.Now;
                    t.PlayCount++;
                    _libraryDirty = true;
                }

                // Recent-play bookkeeping (skip suppressed during session restore).
                if (!ReferenceEquals(t, _lastRecentTrack) && !_suppressNextRecent)
                {
                    _lastRecentTrack = t;
                    PushRecent(t);
                }
                _suppressNextRecent = false;

                // Last.fm scrobble: only when the track has been played past the halfway point.
                var dur = _playback.Duration;
                if (dur.TotalSeconds > 30 && _playback.Position.TotalSeconds >= dur.TotalSeconds / 2.0)
                {
                    EnqueueScrobble(t);
                }
            }
        }

        _suppressNextRecent = false;
    }

    // Accent-titles the row of the track that is currently playing (lists read
    // Track.IsCurrent through CurrentTrackBrushConverter). O(1): toggles only
    // the previous and the new track.
    private Track? _highlightedTrack;

    private void SetCurrentTrackHighlight(Track? track)
    {
        if (ReferenceEquals(_highlightedTrack, track))
            return;
        if (_highlightedTrack != null)
            _highlightedTrack.IsCurrent = false;
        _highlightedTrack = track;
        if (track != null)
            track.IsCurrent = true;
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
            if (q != null && index >= 0 && index < q.Count
                && !ReferenceEquals(q[index], _lastRecentTrack))
            {
                _lastRecentTrack = q[index];
                var t = q[index];
                t.LastPlayed = DateTime.Now;
                PushRecent(t);
            }
        }

        var queue = _playback.Queue;
        SetCurrentTrackHighlight(queue != null && index >= 0 && index < queue.Count ? queue[index] : null);

        LoadLyricsFor(index);

        QueueList.SelectedIndex = _playback.CurrentIndex;
        ScrollQueueToCurrent();
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

        // Scrobble: if the track crosses the halfway point during playback,
        // scrobble it (covers seeks past the half-point too).
        if (_isPlaying && dur.TotalSeconds > 30 && pos.TotalSeconds >= dur.TotalSeconds / 2.0)
        {
            var q = _playback.Queue;
            var idx = _playback.CurrentIndex;
            if (q != null && idx >= 0 && idx < q.Count)
                EnqueueScrobble(q[idx]);
        }

        if ((DateTime.Now - _lastProgressSave).TotalSeconds >= 3)
        {
            _lastProgressSave = DateTime.Now;
            PlaylistStore.SaveProgress(_playback.CurrentIndex, pos, CurrentPath());

            // Persist PlayCount / Favorite changes at the same cadence. Off the
            // UI thread: serializing the whole library mid-playback stutters.
            if (_libraryDirty)
            {
                PersistLibraryBackground();
                _libraryDirty = false;
            }
        }
    }

    private string? CurrentPath()
    {
        var q = _playback.Queue;
        if (q != null && _playback.CurrentIndex >= 0 && _playback.CurrentIndex < q.Count)
            return q[_playback.CurrentIndex]?.Path;
        return null;
    }

    // ---------- Last.fm scrobbling ----------

    private void EnqueueScrobble(Track track)
    {
        if (!_lastFm.IsConnected)
            return;

        // Dedup: only scrobble each track once per session.
        var key = $"{track.Artist}\0{track.Title}\0{track.Path}";
        if (!_scrobbledThisSession.Add(key))
            return;

        var entry = new ScrobbleEntry
        {
            Artist = track.Artist,
            Track = track.Title,
            Album = track.Album,
            TimestampUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Duration = (int)_playback.Duration.TotalSeconds
        };
        _scrobbleQueue.Add(entry);

        // Debounce: flush after 2 seconds of no new scrobbles.
        if (!_scrobbleTimerRunning)
        {
            _scrobbleTimerRunning = true;
            _scrobbleTimer.Start();
        }
        else
        {
            _scrobbleTimer.Stop();
            _scrobbleTimer.Start();
        }
    }

    private void FlushScrobbleQueue()
    {
        _scrobbleTimer.Stop();
        _scrobbleTimerRunning = false;

        if (_scrobbleQueue.Count == 0)
            return;

        var batch = new List<ScrobbleEntry>(_scrobbleQueue);
        _scrobbleQueue.Clear();
        _ = Task.Run(async () => await _lastFm.ScrobbleBatchAsync(batch));
    }

    // ---------- Lyrics ----------

    private void LoadLyricsFor(int index)
    {
        _loadedIndex = index;
        _currentLineIndex = -1;
        _lyricScrollTarget = null;
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

        // Highlight + center right away (e.g. session restore while paused)
        // instead of waiting for a position tick that may never arrive.
        UpdateLyricHighlight(_playback.Position);
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

    // Alternate between the two cover layers on every change, so switching
    // tracks crossfades instead of hard-cutting the artwork.
    private bool _coverFrontIsA = true;

    private void ApplyCover(ImageSource? cover)
    {
        var newBrush = cover == null
            ? CoverPlaceholder
            : new ImageBrush { ImageSource = cover };

        var back = _coverFrontIsA ? NowCoverEllipseB : NowCoverEllipse;
        var front = _coverFrontIsA ? NowCoverEllipse : NowCoverEllipseB;
        _coverFrontIsA = !_coverFrontIsA;

        back.Fill = newBrush;

        var sb = new Storyboard();
        var fadeIn = new DoubleAnimation { To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(220)) };
        Storyboard.SetTarget(fadeIn, back);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        var fadeOut = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(220)) };
        Storyboard.SetTarget(fadeOut, front);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        sb.Children.Add(fadeIn);
        sb.Children.Add(fadeOut);
        sb.Begin();
    }

    private void ResetNowPlaying()
    {
        SetCurrentTrackHighlight(null);
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
        // Nothing is playing, so the next play is a fresh entry even if it
        // happens to be the same track again.
        _lastRecentTrack = null;
        _lastCountedTrack = null;
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
            Opacity = 0.45,
            // Opacity + Scale animate smoothly when a line activates, instead
            // of hard-switching (see SetLineActive).
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(180) },
            ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(180) },
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
            _lyricScrollTarget = _lyricPanels[idx];
            _dispatcher.TryEnqueue(ScrollLyricToCurrent);
        }

        PushDesktop(idx);
    }

    /// <summary>
    /// Center the current lyric line in the lyric ScrollViewer. Runs through a
    /// dispatcher pass so freshly-built panels have real offsets; forces a
    /// synchronous layout first because ActualOffset is stale until measured.
    /// </summary>
    private void ScrollLyricToCurrent()
    {
        var target = _lyricScrollTarget;
        if (target == null)
            return;

        target.UpdateLayout();
        LyricScroll.UpdateLayout();

        // No layout yet (now-playing panel still collapsed) → nothing to scroll.
        if (LyricScroll.ActualHeight <= 0 || target.ActualHeight <= 0)
            return;

        var top = target.ActualOffset.Y - LyricScroll.ActualHeight / 2 + target.ActualHeight / 2;
        if (top > 0)
            LyricScroll.ChangeView(null, top, null);
    }

    // Last payload handed to the overlay. The position tick fires several times
    // a second, but the text only changes when the line does — without this the
    // overlay is serialized and pushed over the pipe on every single tick.
    private Track? _pushedTrack;
    private string? _pushedOriginal;
    private string? _pushedRoma;
    private string? _pushedTrans;

    private void PushDesktop(int idx)
    {
        if (_desktopLyrics == null || idx < 0 || _lyrics == null)
            return;
        var line = _lyrics.Lines[idx];
        var roma = _settings.LyricShowRomaji ? line.Romaji : null;
        var trans = _settings.LyricShowTranslation ? line.Translation : null;
        var original = line.Original ?? string.Empty;

        // The track is part of the comparison too: switching songs must push
        // even when the first line happens to be identical.
        if (ReferenceEquals(_currentTrack, _pushedTrack)
            && string.Equals(original, _pushedOriginal, StringComparison.Ordinal)
            && string.Equals(roma, _pushedRoma, StringComparison.Ordinal)
            && string.Equals(trans, _pushedTrans, StringComparison.Ordinal))
            return;

        _pushedTrack = _currentTrack;
        _pushedOriginal = original;
        _pushedRoma = roma;
        _pushedTrans = trans;
        _desktopLyrics.UpdateLyric(_currentTrack, original, roma, trans);
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
    /// <summary>
    /// Persist a lyric download next to the audio file, honouring the user's
    /// fill-mode preference:
    ///   All        – main lyric + translation + romaji
    ///   MainOnly   – only the main lyric; translation/romaji left untouched
    ///   ExtrasOnly – only translation/romaji; the main lyric is never overwritten
    /// Returns true when at least one file was written.
    /// </summary>
    private static bool SaveLyricFiles(
        Track track, string? lyric, string? trans, string? roma, LyricFillModeKind fillMode)
    {
        bool writeMain = fillMode is LyricFillModeKind.All or LyricFillModeKind.MainOnly;
        bool writeExtras = fillMode is LyricFillModeKind.All or LyricFillModeKind.ExtrasOnly;

        var main = writeMain && !string.IsNullOrWhiteSpace(lyric) ? lyric : null;
        var zh = writeExtras && !string.IsNullOrWhiteSpace(trans) ? trans : null;
        var ro = writeExtras && !string.IsNullOrWhiteSpace(roma) ? roma : null;

        if (main == null && zh == null && ro == null)
            return false;

        var basePath = Path.Combine(
            Path.GetDirectoryName(track.Path) ?? "",
            Path.GetFileNameWithoutExtension(track.Path));
        var utf8 = new System.Text.UTF8Encoding(false);

        if (main != null) AtomicFile.WriteAllText(basePath + ".lrc", main, utf8);
        if (zh != null) AtomicFile.WriteAllText(basePath + ".zh.lrc", zh, utf8);
        if (ro != null) AtomicFile.WriteAllText(basePath + ".romaji.lrc", ro, utf8);
        return true;
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
    /// Order is NetEase → QQ → LRCLIB, but the user's LyricSource preference
    /// can restrict this to a single source (see LyricSourceKind) — a specific
    /// choice is used exclusively rather than silently falling back.
    /// NetEase serves original + translation + romaji from ONE source, so
    /// companion-file timestamps line up exactly. QQ Music is second and now
    /// also yields translation + romaji via the encrypted QRC endpoint.
    /// LRCLIB is third (synced LRC with time tags, original only).
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

        // Honour the user's lyric-source preference. "Auto" tries every source in
        // order; a specific source is used exclusively (no silent fallback).
        var sourceKind = LyricPreferences.ParseSource(_settings.LyricSource);
        bool tryNetEase = sourceKind is LyricSourceKind.Auto or LyricSourceKind.NetEase;
        bool tryQq = sourceKind is LyricSourceKind.Auto or LyricSourceKind.QQ;
        bool tryLrclib = sourceKind is LyricSourceKind.Auto or LyricSourceKind.LRCLIB;
        var fillMode = LyricPreferences.ParseFillMode(_settings.LyricFillMode);

        // 1) NetEase (full three-line set).
        var neSong = tryNetEase ? await MatchNetEaseAsync(keyword, track.Title, durationSec) : null;
        if (neSong != null)
        {
            var ne = await NetEaseLyricService.FetchLyricAsync(neSong.SongMid);
            if (ne != null &&
                SaveLyricFiles(track, ne.Value.Lyric, ne.Value.Trans, ne.Value.Roma, fillMode))
                return true;
        }

        // 2) QQ Music fallback (original + translation + romaji via QRC).
        var qqResults = tryQq ? await QQLyricService.SearchAsync(keyword, 20) : new List<QQSong>();
        if (qqResults.Count > 0)
        {
            var primaryArtist = Norm(track.Artist == "未知歌手" ? "" : track.Artist);

            QQSong? best = null;
            var bestScore = int.MinValue;

            foreach (var r in qqResults)
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
            if (best != null && bestScore >= 10)
            {
                var lyric = await QQLyricService.FetchLyricAsync(best.SongMid, best.SongId);
                if (lyric != null &&
                    SaveLyricFiles(track, lyric.Value.Lyric, lyric.Value.Trans, lyric.Value.Roma, fillMode))
                    return true;
            }
        }

        // 3) LRCLIB fallback (synced LRC with time tags, original only).
        var lrResults = tryLrclib ? await LrclibService.SearchAsync(keyword, 20) : new List<QQSong>();
        if (lrResults.Count > 0)
        {
            QQSong? best = null;
            var bestScore = int.MinValue;

            foreach (var r in lrResults)
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
                    continue;

                var primaryArtist = Norm(track.Artist == "未知歌手" ? "" : track.Artist);
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

            if (best != null && bestScore >= 10)
            {
                var lyric = await LrclibService.FetchLyricAsync(best.SongMid);
                if (lyric != null &&
                    SaveLyricFiles(track, lyric.Value.Lyric, lyric.Value.Trans, lyric.Value.Roma, fillMode))
                    return true;
            }
        }

        return false;
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

    private void ShowOnlineLyricDialog(Track track)
        => SafeRun(() => ShowOnlineLyricDialogAsync(track), "下载歌词");

    private async Task<bool> ShowOnlineLyricDialogAsync(Track track)
    {
        // Resizable standalone window (the old fixed-width ContentDialog clipped
        // result rows). It searches with the persisted source preference and
        // returns the confirmed pick, or null when cancelled.
        var win = new OnlineLyricWindow(_settings, BuildSearchKeyword(track));
        win.Activate();
        var picked = await win.Completion;
        if (picked == null)
            return false;
        var selected = picked.Value.Song;
        var source = picked.Value.SourceIndex;
        var sourceName = source switch { 1 => "网易云", 2 => "LRCLIB", _ => "QQ音乐" };

        ShowInfoBar($"正在下载歌词（{sourceName}）：{selected.Title} - {selected.Artist}");
        var lyric = source switch
        {
            1 => await NetEaseLyricService.FetchLyricAsync(selected.SongMid),
            2 => await LrclibService.FetchLyricAsync(selected.SongMid),
            _ => await QQLyricService.FetchLyricAsync(selected.SongMid, selected.SongId),
        };
        if (string.IsNullOrEmpty(lyric?.Lyric))
        {
            ShowInfoBar("该歌曲没有可用歌词。");
            return false;
        }

        track.LyricPath = null; // drop an old manual binding; auto-detect the new file
        string? extraNote = null;
        try
        {
            var fillMode = LyricPreferences.ParseFillMode(_settings.LyricFillMode);
            SaveLyricFiles(track, lyric.Value.Lyric, lyric.Value.Trans, lyric.Value.Roma, fillMode);

            // QQ's QRC feed covers translation + romaji for many tracks, but not
            // all. When something is still missing, top it up from NetEase,
            // snapping those timestamps onto the saved main lyric so the three
            // lines merge correctly. (NetEase / LRCLIB results already carry
            // their own translation / romaji — no top-up needed.)
            if (source == 0)
            {
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
        }
        catch (Exception ex)
        {
            ShowInfoBar($"歌词保存失败：{ex.Message}（歌曲目录可能只读或无写入权限）");
            return false;
        }
        if (_currentTrack == track)
            LoadLyricsFor(_loadedIndex);
        ShowInfoBar($"已保存歌词：{selected.Title} - {selected.Artist}{extraNote}");
        return true;
    }

    // ---------- Batch lyric download + translation/romaji completion ----------

    private bool _batchLyricRunning;

    // ---- Lyric completion queue (download-manager style panel) ----
    private readonly ObservableCollection<LyricTaskItem> _lyricTasks = new();
    private CancellationTokenSource? _lyricBatchCts;
    private string _lyricFilter = "All";

    /// <summary>Guards SyncLyricOptionChecks against re-entrant SelectionChanged events.</summary>
    private bool _syncingLyricOptions;

    /// <summary>
    /// Inspect the track's existing lyrics; if a translation and/or a romaji
    /// set is missing, fetch the missing parts from NetEase Cloud Music and
    /// save them as companion <c>.zh.lrc</c> / <c>.romaji.lrc</c> files next
    /// to the audio (the exact names <see cref="LyricsParser"/> auto-detects).
    ///
    /// NetEase's lyrics almost always carry a different timestamp grid than
    /// the local main file, so the fetched lines are snapped onto the main
    /// lyric's timestamps (within 2.5&nbsp;s) before saving — this guarantees
    /// the three lines merge cleanly by exact time.
    ///
    /// Every step (presence check, NetEase match, snap result, file write,
    /// failures) is appended to <see cref="AppLog.LyricCompletionLogPath"/>.
    /// Returns (translationTopped, romajiTopped).
    /// </summary>
    private async Task<(bool ToppedTrans, bool ToppedRoma)> TryCompleteTranslationAndRomajiAsync(Track track)
    {
        var forcedEnc = _settings.LyricEncoding == "auto" ? null : _settings.LyricEncoding;
        var doc = LyricsParser.Parse(track.Path, track.LyricPath, forcedEnc);
        if (doc == null || doc.Lines.Count == 0)
        {
            AppLog.WriteLyricCompletion("  无主歌词，无法补全翻译/罗马音");
            return (false, false);
        }

        bool hasTrans = doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation));
        bool hasRoma = doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Romaji));
        AppLog.WriteLyricCompletion(
            $"  翻译：{(hasTrans ? "已存在" : "缺失")}，罗马音：{(hasRoma ? "已存在" : "缺失")}");

        if (hasTrans && hasRoma)
        {
            AppLog.WriteLyricCompletion("  结果：翻译和罗马音均已齐全，跳过");
            return (false, false);
        }

        // Master timestamp grid: only lines that actually carry original lyric
        // text — orphan companion-only lines would create wrong snap targets.
        var master = doc.Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Original))
            .Select(l => l.Time)
            .ToList();
        if (master.Count == 0)
        {
            AppLog.WriteLyricCompletion("  无法从主歌词提取时间轴，跳过");
            return (false, false);
        }

        var keyword = BuildSearchKeyword(track);
        if (keyword.Length == 0)
        {
            AppLog.WriteLyricCompletion("  无法构造搜索关键词，跳过");
            return (false, false);
        }

        int? durationSec = track.Duration > TimeSpan.Zero ? (int)track.Duration.TotalSeconds : null;
        var neSong = await MatchNetEaseAsync(keyword, track.Title, durationSec);
        if (neSong == null)
        {
            AppLog.WriteLyricCompletion("  NetEase 无匹配歌曲，无法补全");
            return (false, false);
        }
        AppLog.WriteLyricCompletion(
            $"  NetEase 匹配：{neSong.Title} - {neSong.Artist} (id={neSong.SongMid})");

        var ne = await NetEaseLyricService.FetchLyricAsync(neSong.SongMid);
        if (ne == null)
        {
            AppLog.WriteLyricCompletion("  NetEase 歌词获取失败");
            return (false, false);
        }

        var basePath = Path.Combine(
            Path.GetDirectoryName(track.Path) ?? "",
            Path.GetFileNameWithoutExtension(track.Path));
        var utf8 = new System.Text.UTF8Encoding(false);

        bool toppedTrans = false, toppedRoma = false;

        if (!hasTrans)
        {
            var trans = SnapToMaster(ne?.Trans, master);
            if (!string.IsNullOrEmpty(trans))
            {
                try
                {
                    AtomicFile.WriteAllText(basePath + ".zh.lrc", trans!, utf8);
                    toppedTrans = true;
                    AppLog.WriteLyricCompletion($"  翻译已补全：{basePath}.zh.lrc");
                }
                catch (Exception ex)
                {
                    AppLog.WriteLyricCompletion($"  翻译保存失败：{ex.Message}");
                }
            }
            else
            {
                AppLog.WriteLyricCompletion("  NetEase 无翻译，或无法对齐到主歌词时间轴");
            }
        }

        if (!hasRoma)
        {
            var roma = SnapToMaster(ne?.Roma, master);
            if (!string.IsNullOrEmpty(roma))
            {
                try
                {
                    AtomicFile.WriteAllText(basePath + ".romaji.lrc", roma!, utf8);
                    toppedRoma = true;
                    AppLog.WriteLyricCompletion($"  罗马音已补全：{basePath}.romaji.lrc");
                }
                catch (Exception ex)
                {
                    AppLog.WriteLyricCompletion($"  罗马音保存失败：{ex.Message}");
                }
            }
            else
            {
                AppLog.WriteLyricCompletion("  NetEase 无罗马音，或无法对齐到主歌词时间轴");
            }
        }

        return (toppedTrans, toppedRoma);
    }

    /// <summary>
    /// Classify every track in the current list into one of three buckets:
    ///   - no lyric   : no main lyric file at all            → download fresh
    ///   - incomplete : main lyric present but lacks translation and/or romaji
    ///                                                       → top up missing parts
    ///   - complete   : all three parts present              → skipped entirely
    /// Touches the disk once per track, so call it via Task.Run.
    /// </summary>
    private (List<Track> NoLyric, List<Track> Incomplete) ClassifyLyricTracks()
    {
        var noLyric = new List<Track>();
        var incomplete = new List<Track>();
        var forcedEnc = _settings.LyricEncoding == "auto" ? null : _settings.LyricEncoding;

        foreach (var t in _activeTracks)
        {
            if (!HasLocalLyric(t))
            {
                noLyric.Add(t);
                continue;
            }

            var doc = LyricsParser.Parse(t.Path, t.LyricPath, forcedEnc);
            if (doc == null || doc.Lines.Count == 0)
            {
                // A lyric file exists but could not be parsed — treat as missing
                // so the auto-download path can replace it.
                noLyric.Add(t);
                continue;
            }

            bool hasTrans = doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation));
            bool hasRoma = doc.Lines.Any(l => !string.IsNullOrWhiteSpace(l.Romaji));
            if (!hasTrans || !hasRoma)
                incomplete.Add(t);
        }

        return (noLyric, incomplete);
    }

    /// <summary>
    /// Runs the one-click lyric fill. Wired to the SplitButton's primary click,
    /// so it takes SplitButtonClickEventArgs; the drop-down half only carries
    /// options (lyric source / what to fill). The actual work now runs through
    /// the completion page's queue so progress is visible.
    /// </summary>
    private async void BtnBatchLyrics_Click(object sender, SplitButtonClickEventArgs e)
    {
        // Jump to the completion page so the run is visible as it happens.
        ShowView(NavView.LyricFill);
        await StartLyricCompletionAsync();
    }

    private async void BtnLyricStart_Click(object sender, RoutedEventArgs e)
        => await StartLyricCompletionAsync();

    private void BtnLyricStop_Click(object sender, RoutedEventArgs e)
    {
        _lyricBatchCts?.Cancel();
        BtnLyricStop.IsEnabled = false;
    }

    private async void BtnLyricRetry_Click(object sender, RoutedEventArgs e)
    {
        // Only the rows that didn't make it — successes are left alone.
        var retry = _lyricTasks
            .Where(t => t.Status is LyricTaskStatus.Failed or LyricTaskStatus.Cancelled)
            .ToList();
        if (retry.Count == 0) return;

        foreach (var item in retry) SetLyricTaskState(item, LyricTaskStatus.Pending, "等待中");
        await RunLyricQueueAsync(retry);
    }

    private void LyricFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _lyricFilter = LyricFilterCombo.SelectedIndex switch
        {
            1 => "Failed",
            2 => "Success",
            _ => "All",
        };
        ApplyLyricFilter();
    }

    /// <summary>Binds the queue (filtered) to the list. The rows are the same
    /// LyricTaskItem instances, so live status updates keep flowing through.</summary>
    private void ApplyLyricFilter()
    {
        // The ComboBox declares SelectedIndex="0" in XAML, so this fires during
        // InitializeComponent — before the ListView further down the tree exists.
        if (LyricTaskList == null) return;

        IEnumerable<LyricTaskItem> src = _lyricFilter switch
        {
            "Failed" => _lyricTasks.Where(t => t.Status == LyricTaskStatus.Failed),
            "Success" => _lyricTasks.Where(t => t.Status == LyricTaskStatus.Success),
            _ => _lyricTasks,
        };
        LyricTaskList.ItemsSource = new ObservableCollection<LyricTaskItem>(src);
    }

    /// <summary>Scans the library, builds the queue and runs it.</summary>
    private async Task StartLyricCompletionAsync()
    {
        if (_batchLyricRunning) return;

        // Classification stats every track on disk (File.Exists checks plus a
        // full parse of each lyric file), which is seconds of blocking I/O on a
        // large list — run it off the UI thread so the window stays responsive.
        LyricStatusText.Text = "正在扫描曲库…";
        var (noLyric, incomplete) = await Task.Run(ClassifyLyricTracks);

        if (noLyric.Count == 0 && incomplete.Count == 0)
        {
            LyricStatusText.Text = "所有歌曲的歌词（含翻译和罗马音）均已齐全。";
            ShowInfoBar("所有歌曲的歌词（含翻译和罗马音）均已齐全。");
            return;
        }

        _lyricTasks.Clear();
        foreach (var t in noLyric)
            _lyricTasks.Add(new LyricTaskItem(t) { NeedsMainLyric = true, Detail = "等待下载" });
        foreach (var t in incomplete)
            _lyricTasks.Add(new LyricTaskItem(t) { NeedsMainLyric = false, Detail = "等待补全" });
        foreach (var item in _lyricTasks) StampLyricTaskBrush(item);
        ApplyLyricFilter();
        UpdateLyricSummary();

        AppLog.WriteLyricCompletionSection(
            $"补全歌词开始 — 共 {_lyricTasks.Count} 首" +
            $"（缺主歌词 {noLyric.Count}，缺翻译/罗马音 {incomplete.Count}）");

        await RunLyricQueueAsync(_lyricTasks.ToList());
    }

    /// <summary>
    /// Walks the queue, updating each row as it goes. Cancellable: stopping
    /// leaves finished tracks alone and marks the remainder as cancelled.
    /// </summary>
    private async Task RunLyricQueueAsync(IReadOnlyList<LyricTaskItem> queue)
    {
        _batchLyricRunning = true;
        _lyricBatchCts = new CancellationTokenSource();
        var ct = _lyricBatchCts.Token;
        // Filtering mid-run would make rows jump around, so lock it while busy.
        LyricFilterCombo.IsEnabled = false;
        BtnLyricStart.IsEnabled = false;
        BtnLyricStop.IsEnabled = true;
        BtnLyricRetry.IsEnabled = false;

        int downloaded = 0, topped = 0, failed = 0;
        var refreshCurrent = false;
        var i = 0;

        try
        {
            foreach (var item in queue)
            {
                if (ct.IsCancellationRequested)
                {
                    SetLyricTaskState(item, LyricTaskStatus.Cancelled, "已停止");
                    continue;
                }

                i++;
                var t = item.Track;
                SetLyricTaskState(item, LyricTaskStatus.Running, "处理中…");
                LyricStatusText.Text = $"[{i}/{queue.Count}] {t.Title} — {t.Artist}";
                AppLog.WriteLyricCompletion($"[{i}/{queue.Count}] {(item.NeedsMainLyric ? "缺主歌词" : "检查翻译/罗马音")}：{t.Title} - {t.Artist}");
                AppLog.WriteLyricCompletion($"  音频：{t.Path}");

                try
                {
                    if (item.NeedsMainLyric)
                    {
                        if (await TryAutoDownloadAsync(t))
                        {
                            downloaded++;
                            if (_currentTrack == t) refreshCurrent = true;
                            AppLog.WriteLyricCompletion("  主歌词下载成功，检查翻译/罗马音…");
                            var (tt, tr) = await TryCompleteTranslationAndRomajiAsync(t);
                            if (tt || tr) topped++;
                            SetLyricTaskState(item, LyricTaskStatus.Success,
                                tt || tr ? "已下载 + 补翻译/罗马音" : "已下载歌词");
                        }
                        else
                        {
                            failed++;
                            AppLog.WriteLyricCompletion("  结果：未找到匹配的歌词");
                            SetLyricTaskState(item, LyricTaskStatus.Failed, "未找到匹配的歌词");
                        }
                    }
                    else
                    {
                        var (tt, tr) = await TryCompleteTranslationAndRomajiAsync(t);
                        if (tt || tr)
                        {
                            topped++;
                            if (_currentTrack == t) refreshCurrent = true;
                            SetLyricTaskState(item, LyricTaskStatus.Success, "已补翻译/罗马音");
                        }
                        else
                        {
                            failed++;
                            SetLyricTaskState(item, LyricTaskStatus.Failed, "无可用翻译/罗马音");
                        }
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    AppLog.WriteLyricCompletion($"  异常：{ex.Message}");
                    SetLyricTaskState(item, LyricTaskStatus.Failed, ex.Message);
                }

                // Be polite to the API — but bail out immediately on cancel.
                try { await Task.Delay(400, ct); }
                catch (OperationCanceledException) { }
            }

            if (refreshCurrent)
                LoadLyricsFor(_loadedIndex);

            var stopped = ct.IsCancellationRequested;
            var summary = stopped
                ? $"补全已停止：新下载 {downloaded}，补全翻译/罗马音 {topped}，失败 {failed}"
                : $"补全歌词完成：新下载 {downloaded}，补全翻译/罗马音 {topped}，失败 {failed} / {queue.Count}";
            AppLog.WriteLyricCompletionSection(summary);
            LyricStatusText.Text = summary;
            ShowInfoBar(summary + $"。日志：{AppLog.LyricCompletionLogPath}");
        }
        catch (Exception ex)
        {
            ShowInfoBar($"补全歌词失败：{ex.Message}");
        }
        finally
        {
            // Must run even on failure: an `async void` handler has no caller to
            // catch the exception, and leaving the guard set would permanently
            // lock the action out until the app is restarted.
            _batchLyricRunning = false;
            _lyricBatchCts?.Dispose();
            _lyricBatchCts = null;
            LyricFilterCombo.IsEnabled = true;
            UpdateLyricSummary();
        }
    }

    /// <summary>
    /// Right-click a row in the completion list to manually complete it —
    /// either an online search (the same dialog the track context menu uses)
    /// or a local lyric file. A successful assignment marks the row as done so
    /// "retry failed" won't re-run tracks the user fixed by hand.
    /// </summary>
    private void LyricTask_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: LyricTaskItem item })
            return;
        e.Handled = true;

        var search = new MenuFlyoutItem
        {
            Text = "手动搜索歌词...",
            Icon = new FontIcon { Glyph = "\uE721" }
        };
        search.Click += async (_, _) =>
        {
            if (await ShowOnlineLyricDialogAsync(item.Track))
                SetLyricTaskState(item, LyricTaskStatus.Success, "手动搜索补全");
        };

        var assign = new MenuFlyoutItem
        {
            Text = "指定本地歌词文件...",
            Icon = new FontIcon { Glyph = "\uE8E5" }
        };
        assign.Click += async (_, _) =>
        {
            if (await AssignLyricToTrackAsync(item.Track))
                SetLyricTaskState(item, LyricTaskStatus.Success, "已指定本地歌词");
        };

        new MenuFlyout { Items = { search, assign } }
            .ShowAt(sender as FrameworkElement, e.GetPosition(sender as FrameworkElement));
    }

    /// <summary>Sets one row's state and re-stamps the summary.</summary>
    private void SetLyricTaskState(LyricTaskItem item, LyricTaskStatus status, string detail)    {
        item.Status = status;
        item.Detail = detail;
        StampLyricTaskBrush(item);
        UpdateLyricSummary();
    }

    /// <summary>
    /// Status colours live in ThemeDictionaries, which the plain resource
    /// indexer can't see, so the brush is resolved here and handed to the row.
    /// Re-stamped for every row when the theme flips.
    /// </summary>
    private void StampLyricTaskBrush(LyricTaskItem item)
    {
        var key = item.Status switch
        {
            LyricTaskStatus.Running => "QqGreen",
            LyricTaskStatus.Success => "TextSuccess",
            LyricTaskStatus.Failed => "TextDanger",
            _ => "TextMuted",
        };
        item.StatusBrush = FindResource(key) as Microsoft.UI.Xaml.Media.Brush;
    }

    private void UpdateLyricSummary()
    {
        int total = _lyricTasks.Count;
        int ok = _lyricTasks.Count(t => t.Status == LyricTaskStatus.Success);
        int bad = _lyricTasks.Count(t => t.Status == LyricTaskStatus.Failed);
        int done = _lyricTasks.Count(t => t.Status is LyricTaskStatus.Success
                                            or LyricTaskStatus.Failed
                                            or LyricTaskStatus.Cancelled);

        LyricProgressBar.Maximum = total == 0 ? 1 : total;
        LyricProgressBar.Value = done;
        LyricCountText.Text = total == 0 ? "共 0 首" : $"{done}/{total}　成功 {ok}　失败 {bad}";

        BtnLyricStart.IsEnabled = !_batchLyricRunning;
        BtnLyricStop.IsEnabled = _batchLyricRunning;
        BtnLyricRetry.IsEnabled = !_batchLyricRunning && bad > 0;
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

    /// <summary>
    /// Run an async UI action and surface any failure to the user instead of
    /// letting it escape. Exceptions thrown inside an `async void` handler have
    /// no caller that can catch them and will tear down the process, so every
    /// async entry point (file pickers, network calls, drag-and-drop) routes
    /// through here.
    /// </summary>
    private async void SafeRun(Func<Task> action, string what)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ShowInfoBar($"{what}失败：{ex.Message}");
        }
    }

    private static void SetLineActive(StackPanel panel, bool active)
    {
        foreach (var child in panel.Children)
        {
            if (child is TextBlock tb)
            {
                tb.Opacity = active ? 1.0 : 0.45;
                tb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
                // Scale is center-anchored and animated via ScaleTransition —
                // the active line gently grows instead of popping.
                tb.Scale = active
                    ? new System.Numerics.Vector3(1.03f, 1.03f, 1f)
                    : System.Numerics.Vector3.One;
            }
        }
    }

    // ---------- Settings ----------

    private void ShowSettings()
    {
        // Pushing persisted values into the controls must not echo back out
        // as "changes" (see _suppressSettingEvents).
        _suppressSettingEvents = true;
        try
        {
            SettingsUiCore();
        }
        finally
        {
            _suppressSettingEvents = false;
        }
    }

    private void SettingsUiCore()
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
        // Restores both the settings combo boxes and the toolbar's radio menus.
        SyncLyricOptionChecks();
        CoverSpinToggle.IsOn = _settings.CoverSpin;
        DynamicVolumeToggle.IsOn = _settings.DynamicVolume;
        TitlePreferFilenameToggle.IsOn = _settings.TitlePreferFilename;
        QqCookieBox.Text = _settings.QqCookie;
        CloseActionCombo.SelectedIndex = _settings.CloseAction == "Tray" ? 1 : 0;

        // Sync auto-start from registry (source of truth).
        _settings.AutoStart = AutoStart.IsAutoStartEnabled();
        AutoStartToggle.IsOn = _settings.AutoStart;

        AccentColorPicker.Color = ParseHex(string.IsNullOrEmpty(_settings.AccentColor) ? "#31c27c" : _settings.AccentColor);

        // Data/cache location.
        CacheDirBox.Text = DataLocation.Root;
        CacheDirStatus.Text = DataLocation.IsCustom
            ? "当前为自定义位置（默认：%LOCALAPPDATA%\\MusicPlayer）。"
            : "当前使用默认位置：%LOCALAPPDATA%\\MusicPlayer。";
        if (FindResource("TextSecondary") is Microsoft.UI.Xaml.Media.Brush dim)
            CacheDirStatus.Foreground = dim;

        // Last.fm connection state.
        LastFmApiKeyBox.Text = _settings.LastFmApiKey;
        LastFmApiSecretBox.Text = _settings.LastFmApiSecret;
        UpdateLastFmUi();
    }

    private void UpdateLastFmUi()
    {
        if (_lastFm.IsConnected)
        {
            LastFmConnectPanel.Visibility = Visibility.Collapsed;
            LastFmDisconnectPanel.Visibility = Visibility.Visible;
            LastFmUsernameText.Text = $"已连接：{_lastFm.Username}";
        }
        else
        {
            LastFmConnectPanel.Visibility = Visibility.Visible;
            LastFmDisconnectPanel.Visibility = Visibility.Collapsed;
            LastFmStatus.Text = string.IsNullOrEmpty(_settings.LastFmApiKey) ? "" : "未连接";
        }
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

    private void LyricSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.LyricSource = LyricPreferences.ToSetting(LyricSourceCombo.SelectedIndex switch
        {
            1 => LyricSourceKind.NetEase,
            2 => LyricSourceKind.QQ,
            3 => LyricSourceKind.LRCLIB,
            _ => LyricSourceKind.Auto
        });
        SettingsStore.Save(_settings);
        SyncLyricOptionChecks();
    }

    private void LyricFillModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.LyricFillMode = LyricPreferences.ToSetting(LyricFillModeCombo.SelectedIndex switch
        {
            1 => LyricFillModeKind.MainOnly,
            2 => LyricFillModeKind.ExtrasOnly,
            _ => LyricFillModeKind.All
        });
        SettingsStore.Save(_settings);
        SyncLyricOptionChecks();
    }

    private void LyricSourceOption_Click(object sender, RoutedEventArgs e)
    {
        // ReferenceEquals: we are deliberately identifying which menu item was
        // clicked, so this really is an identity comparison (not value equality).
        var kind = ReferenceEquals(sender, LyricSrcNetEase) ? LyricSourceKind.NetEase
            : ReferenceEquals(sender, LyricSrcQq) ? LyricSourceKind.QQ
            : ReferenceEquals(sender, LyricSrcLrclib) ? LyricSourceKind.LRCLIB
            : LyricSourceKind.Auto;
        _settings.LyricSource = LyricPreferences.ToSetting(kind);
        SettingsStore.Save(_settings);
        SyncLyricOptionChecks();
    }

    private void LyricFillOption_Click(object sender, RoutedEventArgs e)
    {
        var kind = ReferenceEquals(sender, LyricFillMain) ? LyricFillModeKind.MainOnly
            : ReferenceEquals(sender, LyricFillExtras) ? LyricFillModeKind.ExtrasOnly
            : LyricFillModeKind.All;
        _settings.LyricFillMode = LyricPreferences.ToSetting(kind);
        SettingsStore.Save(_settings);
        SyncLyricOptionChecks();
    }

    /// <summary>
    /// Reflect the persisted lyric preferences onto the toolbar's radio menus
    /// AND the settings combo boxes, so the two UIs never disagree.
    /// </summary>
    private void SyncLyricOptionChecks()
    {
        // Assigning ComboBox.SelectedIndex raises SelectionChanged, whose handler
        // calls back into this method. Bail out on re-entry so one user action
        // cannot cascade into repeated settings writes.
        if (_syncingLyricOptions)
            return;
        _syncingLyricOptions = true;
        try
        {
            SyncLyricOptionChecksCore();
        }
        finally
        {
            _syncingLyricOptions = false;
        }
    }

    private void SyncLyricOptionChecksCore()
    {
        var src = LyricPreferences.ParseSource(_settings.LyricSource);
        LyricSrcAuto.IsChecked = src == LyricSourceKind.Auto;
        LyricSrcNetEase.IsChecked = src == LyricSourceKind.NetEase;
        LyricSrcQq.IsChecked = src == LyricSourceKind.QQ;
        LyricSrcLrclib.IsChecked = src == LyricSourceKind.LRCLIB;

        var fill = LyricPreferences.ParseFillMode(_settings.LyricFillMode);
        LyricFillAll.IsChecked = fill == LyricFillModeKind.All;
        LyricFillMain.IsChecked = fill == LyricFillModeKind.MainOnly;
        LyricFillExtras.IsChecked = fill == LyricFillModeKind.ExtrasOnly;

        LyricSourceCombo.SelectedIndex = src switch
        {
            LyricSourceKind.NetEase => 1,
            LyricSourceKind.QQ => 2,
            LyricSourceKind.LRCLIB => 3,
            _ => 0
        };
        LyricFillModeCombo.SelectedIndex = fill switch
        {
            LyricFillModeKind.MainOnly => 1,
            LyricFillModeKind.ExtrasOnly => 2,
            _ => 0
        };
    }

    private void CloseActionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _settings.CloseAction = CloseActionCombo.SelectedIndex == 1 ? "Tray" : "Exit";
        SettingsStore.Save(_settings);
    }

    private void CacheDirBrowse_Click(object sender, RoutedEventArgs e)
        => SafeRun(() => CacheDirBrowseAsync(sender), "选择缓存目录");

    private async Task CacheDirBrowseAsync(object sender)
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

    private void ThemeModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingThemeMode)
            return;
        _settings.ThemeMode = ThemeModeCombo.SelectedIndex == 1 ? "Light" : "Dark";
        SettingsStore.Save(_settings);
        ApplyThemeMode();
    }

    /// <summary>Switches between the dark and light resource dictionaries. Setting
    /// RequestedTheme on the root is what makes every {ThemeResource} in
    /// Themes/SukiTheme.xaml re-resolve, so nothing needs repainting by hand.</summary>
    private void ApplyThemeMode()
    {
        var light = string.Equals(_settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase);
        RootGrid.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;

        // On a live switch, redo the brushes that were assigned from code: unlike
        // {ThemeResource} they are plain property values and won't re-resolve.
        // Skipped on the first pass — the tree has no DataContext yet and the
        // initial ShowView / Loaded handlers set these up correctly anyway.
        if (_themeModeApplied)
        {
            SetNavSelected(_currentView);
            ForEachVisual<FontIcon>(RootGrid, icon =>
            {
                if (icon.DataContext is Track t) UpdateFavIcon(icon, t.Favorite);
            });
            if (FindResource("TextSecondary") is Microsoft.UI.Xaml.Media.Brush dim)
                CacheDirStatus.Foreground = dim;
            foreach (var item in _lyricTasks) StampLyricTaskBrush(item);
        }
        _themeModeApplied = true;

        // Syncing the combo re-enters the selection handler; guard against it.
        _applyingThemeMode = true;
        try
        {
            ThemeModeCombo.SelectedIndex = light ? 1 : 0;
        }
        finally
        {
            _applyingThemeMode = false;
        }
    }

    /// <summary>Resolves a resource key the way the {ThemeResource} markup extension does.
    ///
    /// Colour brushes live in ThemeDictionaries so that flipping RequestedTheme
    /// re-evaluates them — but those keys are NOT visible to the plain
    /// <c>Resources[key]</c> indexer, which throws instead of returning null.
    /// QqGreen / AccentGlow are the exception: they stay in plain resources because
    /// ApplyAccentColor() mutates their Color at runtime.</summary>
    private object? FindResource(string key)
    {
        if (RootGrid.Resources.TryGetValue(key, out var plain)) return plain;

        string theme = RootGrid.RequestedTheme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => Application.Current.RequestedTheme == ApplicationTheme.Light ? "Light" : "Dark",
        };

        return FindInThemeDictionaries(Application.Current.Resources, theme, key);
    }

    private static object? FindInThemeDictionaries(ResourceDictionary root, string theme, string key)
    {
        if (root.ThemeDictionaries.TryGetValue(theme, out var td)
            && td is ResourceDictionary dict
            && dict.TryGetValue(key, out var found))
            return found;

        foreach (var merged in root.MergedDictionaries)
            if (FindInThemeDictionaries(merged, theme, key) is { } nested)
                return nested;

        return null;
    }

    /// <summary>Invokes <paramref name="action"/> on every descendant of type
    /// <typeparamref name="T"/> below <paramref name="root"/>.</summary>
    private static void ForEachVisual<T>(DependencyObject root, Action<T> action) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) action(match);
            ForEachVisual(child, action);
        }
    }

    /// <summary>Applies the chosen theme color live by retinting the shared accent brushes
    /// ("QqGreen" solid + "AccentGlow" gradient first stop) that the whole UI references.</summary>
    private void ApplyAccentColor()
    {
        var color = ParseHex(string.IsNullOrEmpty(_settings.AccentColor) ? "#31c27c" : _settings.AccentColor);
        if (FindResource("QqGreen") is SolidColorBrush qq)
            qq.Color = color;
        if (FindResource("AccentGlow") is LinearGradientBrush glow && glow.GradientStops.Count > 0)
            glow.GradientStops[0].Color = color;
    }

    private bool _coverCollapsed;
    private void BtnCollapseCover_Click(object sender, RoutedEventArgs e)
    {
        _coverCollapsed = !_coverCollapsed;
        // Collapse the whole left column (cover + song/artist/album block) so
        // the lyrics column stretches across the full panel width.
        NowCoverColumn.Visibility = _coverCollapsed ? Visibility.Collapsed : Visibility.Visible;
        CoverChevron.Glyph = _coverCollapsed ? "\uE74F" : "\uE74E"; // down to expand, up to fold
    }

    /// <summary>
    /// True while the settings UI is being initialized programmatically.
    /// Assigning Slider.Minimum/Maximum or SelectedIndex fires the very
    /// handlers that persist user changes — without this guard, initializing
    /// the font slider clobbers the saved value with the coerced default.
    /// </summary>
    private bool _suppressSettingEvents;

    private void LyricFontSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSettingEvents)
            return;
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
        UpdateDiscTimer();
    }

    private void TitlePreferFilenameToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents)
            return;
        _settings.TitlePreferFilename = TitlePreferFilenameToggle.IsOn;
        SettingsStore.Save(_settings);
        MetadataService.PreferFilenameTitles = _settings.TitlePreferFilename;

        // Re-read tags for every track so titles flip immediately, then the
        // INotifyPropertyChanged updates repaint whatever is on screen.
        foreach (var t in _library)
            LoadMetadataFor(t);
    }

    private void QqCookieBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSettingEvents)
            return;
        _settings.QqCookie = QqCookieBox.Text.Trim();
        SettingsStore.Save(_settings);
        QQLyricService.SetCookie(_settings.QqCookie);
    }

    private void DynamicVolumeToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents)
            return;
        _settings.DynamicVolume = DynamicVolumeToggle.IsOn;
        SettingsStore.Save(_settings);
        _playback.LoudnessNormalization = _settings.DynamicVolume;
        // Re-route the current track so the change is heard immediately;
        // playback state and position are preserved across the reload.
        _playback.ReloadCurrent();
    }

    private void AutoStartToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.AutoStart = ((ToggleSwitch)sender).IsOn;
        AutoStart.SetAutoStart(_settings.AutoStart);
        SettingsStore.Save(_settings);
    }

    private void GlobalHotkeysToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.UseGlobalHotkeys = ((ToggleSwitch)sender).IsOn;
        SettingsStore.Save(_settings);
        if (_settings.UseGlobalHotkeys)
            EnableHotkeys();
        else
            DisableHotkeys();
    }

    private void SleepTimerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _sleepTimer.Stop();

        // Index 0 = off, 1 = 15 min, 2 = 30 min, 3 = 60 min, 4 = 90 min.
        var minutes = SleepTimerCombo.SelectedIndex switch
        {
            1 => 15,
            2 => 30,
            3 => 60,
            4 => 90,
            _ => 0
        };

        if (minutes > 0)
        {
            _sleepTimer.Interval = TimeSpan.FromMinutes(minutes);
            _sleepTimer.Start();
            ShowInfoBar($"定时停止播放：{minutes} 分钟后暂停");
        }
    }

    private void OnSleepTimerTick(object? sender, object? e)
    {
        _sleepTimer.Stop();
        _playback.Pause();
        ShowInfoBar("定时停止播放：已暂停");
        // Reset combo to "关" without re-triggering the handler.
        SleepTimerCombo.SelectionChanged -= SleepTimerCombo_SelectionChanged;
        SleepTimerCombo.SelectedIndex = 0;
        SleepTimerCombo.SelectionChanged += SleepTimerCombo_SelectionChanged;
    }

    // ---------- Last.fm ----------

    private void LastFmConnectBtn_Click(object sender, RoutedEventArgs e)
        => SafeRun(() => LastFmConnectAsync(sender), "连接 Last.fm");

    private async Task LastFmConnectAsync(object sender)
    {
        // Save any entered API key/secret first.
        _settings.LastFmApiKey = (LastFmApiKeyBox.Text ?? "").Trim();
        _settings.LastFmApiSecret = (LastFmApiSecretBox.Text ?? "").Trim();
        SettingsStore.Save(_settings);

        if (string.IsNullOrEmpty(_settings.LastFmApiKey) || string.IsNullOrEmpty(_settings.LastFmApiSecret))
        {
            ShowInfoBar("请先填写 Last.fm API Key 和 Secret");
            return;
        }

        LastFmConnectBtn.IsEnabled = false;
        LastFmStatus.Text = "获取授权令牌…";

        try
        {
            var token = await _lastFm.GetTokenAsync();
            if (token == null)
            {
                ShowInfoBar("获取 Last.fm 令牌失败，请检查 API Key");
                LastFmStatus.Text = "连接失败";
                LastFmConnectBtn.IsEnabled = true;
                return;
            }

            // Open the browser for user authorization.
            var authUrl = _lastFm.GetAuthUrl(token);
            // Start returns a Process that owns a handle; nothing here needs it
            // back, and disposing it does not affect the browser that launched.
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true })?.Dispose();

            LastFmStatus.Text = "请在浏览器中授权，完成后点击下方按钮…";

            // Show a dialog asking the user to confirm authorization.
            var dialog = new ContentDialog
            {
                XamlRoot = this.Content.XamlRoot,
                Title = "Last.fm 授权",
                Content = "请在浏览器中完成授权，然后点击「已授权」继续。",
                PrimaryButtonText = "已授权",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                var session = await _lastFm.GetSessionAsync(token);
                if (session != null)
                {
                    ShowInfoBar($"Last.fm 已连接：{session.Value.Username}");
                    // Retry any pending scrobbles.
                    _ = Task.Run(async () => await _lastFm.RetryFailedScrobblesAsync());
                }
                else
                {
                    ShowInfoBar("Last.fm 会话获取失败，请重试");
                }
            }
        }
        catch (Exception ex)
        {
            ShowInfoBar($"Last.fm 连接失败：{ex.Message}");
        }
        finally
        {
            LastFmConnectBtn.IsEnabled = true;
            UpdateLastFmUi();
        }
    }

    private void LastFmDisconnectBtn_Click(object sender, RoutedEventArgs e)
    {
        _lastFm.Disconnect();
        UpdateLastFmUi();
    }

    private void CrossfadeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Index 0 = off, 1 = 1s, 2 = 2s, 3 = 3s.
        var ms = CrossfadeCombo.SelectedIndex switch
        {
            1 => 1000,
            2 => 2000,
            3 => 3000,
            _ => 0
        };
        _settings.CrossfadeDurationMs = ms;
        _playback.CrossfadeDurationMs = ms;
        SettingsStore.Save(_settings);
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

        DisableHotkeys();

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

        // Synchronous, unlike the periodic saves which run in the background: a
        // pending background save may not have finished by now, and PlayCount /
        // Favorite changes would be lost with it.
        PersistLibrary();

        // Persist window geometry + volume.
        var hwnd = WindowNative.GetWindowHandle(this);
        if (NativeMethods.GetWindowRect(hwnd, out var r))
        {
            _settings.WindowW = r.Width;
            _settings.WindowH = r.Height;
            _settings.WindowX = r.Left;
            _settings.WindowY = r.Top;
        }
        // Persist the level the user chose, not the live one: closing the window
        // mid-crossfade (200 ms fade-out, or a multi-second fade-in) would store
        // a value near zero and come back muted on the next launch.
        _settings.Volume = _playback.TargetVolume;
        _settings.PlaybackRate = _playback.Rate;
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
