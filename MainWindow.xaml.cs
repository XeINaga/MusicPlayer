using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
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
    // Word-timed (karaoke) rows, parallel to _lyricPanels: the source line and
    // its gradient brush (null for plain LRC rows). UpdateWordHighlight slides
    // the two-stop gradient across the line for the flowing karaoke sweep.
    private readonly List<Models.LyricLine?> _wordLines = new();
    private readonly List<TextBlock?> _wordOverlays = new(); // accent layer, clipped to sung prefix
    private FrameworkElement? _lyricScrollTarget;
    private int _currentLineIndex = -1;
    private int _loadedIndex = -1;
    private int _lyricsLoadToken; // invalidates in-flight background lyric parses

    /// <summary>
    /// Re-load the lyrics of the track that is playing RIGHT NOW. Callers that
    /// react to lyric-file changes (encoding switch, manual assignment,
    /// downloads) must resolve the index fresh: _loadedIndex goes stale after
    /// SetIndexSilent (queue drag-reorder) or ReplaceQueueSilent (play-next),
    /// and the old "LoadLyricsFor(_loadedIndex)" then loaded ANOTHER track's
    /// lyrics over the now-playing panel.
    /// </summary>
    private void ReloadLyricsForCurrent()
    {
        var track = _currentTrack;
        var q = _playback.Queue;
        var idx = (track != null && q != null) ? q.IndexOf(track) : -1;
        LoadLyricsFor(idx);
    }

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
    private string _sortBy = "Default";   // Default|Title|Artist|Album|DateAdded|Duration|PlayCount

    // Last.fm scrobbling.
    private readonly LastFmService _lastFm;
    private readonly List<ScrobbleEntry> _scrobbleQueue = new();
    private bool _scrobbleTimerRunning;
    private readonly DispatcherTimer _scrobbleTimer = new();

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

    private List<AlbumInfo>? _albumGridCache;
    private List<ArtistInfo>? _artistGridCache;
    private bool _groupCacheDirty = true;

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
    // Play counting: the track currently armed for a count, and where its
    // playback started (seconds). A play is counted in OnPositionTick when the
    // track survives past the listen threshold (30s, or half its duration for
    // very short files) AND started before it — so random-mode skips and
    // launch-resumes (which start past the threshold) never inflate counts.
    private Track? _pendingCountTrack;
    private double _pendingCountStartSec;
    // Scrobble eligibility is tracked SEPARATELY from the play counter: the
    // counter clears itself at the 30s threshold, which would make the
    // halfway-point scrobble unreachable for any track longer than a minute —
    // and the stale armed reference made every position tick re-enqueue the
    // same scrobble (hundreds of duplicates per track). One-shot flag instead.
    private Track? _pendingScrobbleTrack;
    private double _pendingScrobbleStartSec;
    private bool _scrobbleQueued;
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
    // Coalesces settings.json writes from hot paths (color picker, sliders,
    // overlay drag reports). 500ms after the last change the timer flushes.
    private readonly DispatcherTimer _settingsSaveTimer = new();
    private readonly DispatcherTimer _sleepTimer = new();
    private readonly DispatcherTimer _searchDebounceTimer = new();
    // Sound-effect window changes land here: one reload 600ms after the last
    // knob stops moving, so dragging a slider doesn't rebuild the source
    // dozens of times. The filter chain only exists inside the media source,
    // so a change must be heard via a reload of the current track.
    private readonly DispatcherTimer _soundFxApplyTimer = new();
    private int _displayRefreshVersion;
    private readonly CancellationTokenSource _metadataCts = new();
    private static readonly Brush CoverPlaceholder =
        new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x15, 0x15, 0x1c));

    public MainWindow()
    {
        this.InitializeComponent();

        // Window background material (Mica / Mica Alt / acrylic / thin
        // acrylic) is applied by WindowMaterialService after the theme — the
        // semi-transparent MainGlassBg root tint lets it show through.

        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // Sound-effect DSP state mirrors the persisted settings; PlaybackService
        // reads it every track load to decide the decoder route + filter chain.
        SoundFx.LoadFrom(_settings);

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
            _ = RefreshDisplayAsync(true);
        };

        _soundFxApplyTimer.Interval = TimeSpan.FromMilliseconds(600);
        _soundFxApplyTimer.Tick += OnSoundFxApplyTick;

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

        // Then the window background material (needs the theme to map the
        // SystemBackdropConfiguration correctly on first attach).
        ApplyWindowMaterial();

        // Startup material race: attaching the backdrop controller from the
        // constructor — before the window has ever been activated — renders
        // with the wrong policy (observed: the saved thin acrylic only
        // appeared after a manual material re-switch). Re-apply once on the
        // first activation, when the window is fully live; that is the exact
        // same condition a manual switch runs under.
        _materialReappliedOnActivate = false;
        Activated += OnFirstActivatedReapplyMaterial;

        // EchoMusic look: square cover by default, sidebar subtitle, queue badge.
        ApplyCoverMode();
        UpdateProfileSubtitle();
        UpdateQueueBadge();
        UpdateSoundFxIcon();

        // Title bar + profile avatar show the app icon.
        try
        {
            var iconUri = new Uri(Path.Combine(AppContext.BaseDirectory, "AppIcon.ico"));
            TitleAppIcon.Source = new BitmapImage(iconUri);
            ProfileAvatarImage.Source = new BitmapImage(iconUri);
        }
        catch
        {
            // cosmetic only
        }

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
        // AFTER the data dir is applied — CoverCache resolves its folder from
        // DataLocation.Root, and a custom CacheDir used to make the trim scan
        // the default location instead.
        _ = CoverCache.TrimAsync();

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

        // Settings write coalescing (see ScheduleSettingsSave).
        _settingsSaveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _settingsSaveTimer.Tick += (_, _) =>
        {
            _settingsSaveTimer.Stop();
            SettingsStore.Save(_settings);
        };

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

        // Desktop lyrics: reopen automatically when they were on at exit.
        if (_settings.LyricOverlayEnabled)
            BtnDesktopLyrics.IsChecked = true;

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
        ApplyTitleBarTheme(string.Equals(_settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Caption button colours must follow the app theme: the old
    /// hardcoded light-on-dark values made the close/max/min glyphs invisible
    /// on the light title bar.</summary>
    private void ApplyTitleBarTheme(bool light)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var titleBar = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId).TitleBar;

            titleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            if (light)
            {
                titleBar.ButtonForegroundColor = ParseHex("#1d1d1f");
                titleBar.ButtonHoverBackgroundColor = ParseHex("#e8e8ec");
                titleBar.ButtonHoverForegroundColor = ParseHex("#000000");
                titleBar.ButtonPressedBackgroundColor = ParseHex("#dcdce2");
                titleBar.ButtonPressedForegroundColor = ParseHex("#000000");
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveForegroundColor = ParseHex("#9a9aa4");
            }
            else
            {
                titleBar.ButtonForegroundColor = ParseHex("#f2f2f5");
                titleBar.ButtonHoverBackgroundColor = ParseHex("#23232f");
                titleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonPressedBackgroundColor = ParseHex("#2c2c3a");
                titleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                titleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBar.ButtonInactiveForegroundColor = ParseHex("#6a6a76");
            }
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

                // Shuffle bag: restore the pending round from disk so "every
                // track once before any repeat" spans app restarts. The resumed
                // track is excluded by RestoreRandomBag itself.
                if (_playback.Mode == PlayMode.Random)
                    _playback.RestoreRandomBag(
                        PlaylistStore.LoadRandomBag()
                            .Select(p => _library.FirstOrDefault(t => t.Path == p))
                            .Where(t => t != null)
                            .Select(t => t!));
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
                ContentTitle.Text = "偏好设置";
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
        UpdateContentSubtitle();

        // All library views share one collection and RefreshDisplay resets it
        // in place — the ScrollViewer keeps its old pixel offset, which made
        // every view switch land at a seemingly random position. Start at the
        // top instead.
        ScrollTrackListToTop();
    }

    /// <summary>Scroll whichever track list is visible back to the top.</summary>
    private void ScrollTrackListToTop()
    {
        if (TrackGrid.Visibility != Visibility.Visible && TrackList.Visibility != Visibility.Visible)
            return;
        var sv = FindScrollViewerDescendant(TrackGrid.Visibility == Visibility.Visible ? TrackGrid : TrackList);
        if (sv == null)
            return;
        sv.UpdateLayout();
        sv.ChangeView(null, 0, null, disableAnimation: true);
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
            // EchoMusic-style immersive overlay: covers the title bar and the
            // whole body; the floating player bar below stays usable.
            CenterGrid.Visibility = Visibility.Collapsed;
            _nowPlayingOutSb?.Stop();
            NowPlayingPanel.Opacity = 1;
            NowPlayingPanel.Visibility = Visibility.Visible;
            AnimatePanelIn(NowPlayingPanel, NowPanelTransform, fromX: 0, fromY: 36);
            UpdateBottomBarImmersive(true);
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
            CenterGrid.Visibility = Visibility.Visible;
            AnimateNowPlayingOut();
            UpdateBottomBarImmersive(false);
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

    // EchoMusic selection style: the selected entry tints icon + text with the
    // accent colour instead of a filled pill. Buttons inherit Foreground from
    // the style, so toggling the control's Foreground is enough.
    private Button? _selectedNavButton;

    private void SetNavSelected(NavView view)
    {
        if (_selectedNavButton != null)
            _selectedNavButton.ClearValue(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty);

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
        _selectedNavButton = sel;
        if (sel != null && FindResource("QqGreen") is Microsoft.UI.Xaml.Media.Brush accent)
            sel.Foreground = accent;
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
            "PlayCount" => 6,
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
            6 => "PlayCount",
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

        if (added)
            _groupCacheDirty = true;
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

        if (added)
            _groupCacheDirty = true;
        RefreshDisplay();
    }

    // Caps how many tag reads run at once. Restoring a large library used to
    // start one per track immediately — 5k tracks meant 5k queued Task.Runs
    // followed by 5k separate UI callbacks, which pinned the UI for as long as
    // it took to drain.
    private readonly SemaphoreSlim _metadataGate = new(8);

    private async Task LoadMetadataForAsync(Track track)
    {
        var cancellationToken = _metadataCts.Token;
        var entered = false;
        try
        {
            await _metadataGate.WaitAsync(cancellationToken);
            entered = true;
            await MetadataService.LoadAsync(track, _dispatcher, cancellationToken);
            _groupCacheDirty = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // best-effort: a failed tag read must not tear down the app
        }
        finally
        {
            if (entered)
                _metadataGate.Release();
        }
    }

    private void LoadMetadataFor(Track track) =>
        _ = LoadMetadataForAsync(track);

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
            _groupCacheDirty = true;

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

    /// <summary>Find a track by path, creating + registering it in the library if missing.
    /// Paths in LibraryExclusions (deliberately removed by the user) are never resurrected.</summary>
    private Track? ResolveTrack(string path)
    {
        var existing = _library.FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
            return existing;

        if (_settings.LibraryExclusions.Contains(path, StringComparer.OrdinalIgnoreCase))
            return null; // user removed this on purpose — do not re-add via recent/playlists

        // Never create ghost entries for files that no longer exist: the
        // recent-play list keeps deleted songs alive, and the watched-folder
        // sync then removed the resurrected track on EVERY launch.
        if (!File.Exists(path))
            return null;

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
        _groupCacheDirty = true; // album/artist grids must not show the cleared entries
        RefreshDisplay();
    }

    private void BtnClearRecent_Click(object sender, RoutedEventArgs e)
    {
        _recent.Clear();
        PersistRecent();
        // The recent view may currently be playing through a SNAPSHOT of
        // _recent (StartPlayFromView snapshots to dodge the reindex race) —
        // _activeTracks then points at stale copies and this clear appeared
        // to do nothing on screen. Point it back at the live list.
        if (_currentView == NavView.Recent)
            _activeTracks = _recent;
        RefreshDisplay();
    }

    // ---------- Recent / playlists persistence ----------

    private void LoadRecentFromStore()
    {
        // recent.json drives the ORDER and MEMBERSHIP of the 最近播放 list
        // only. Its per-track fields are a stale snapshot (saved at whatever
        // point PersistRecent last ran) — applying them here resurrected
        // zeroed play counts / un-favorited tracks on every launch. The
        // library (自动歌单) is the single authority for those values.
        foreach (var entry in PlaylistStore.LoadRecent())
        {
            var t = ResolveTrack(entry.Path);
            if (t != null && !_recent.Contains(t))
                _recent.Add(t);
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
                        // Songs living only inside a playlist must pass the
                        // same guards as the rest of the library: skip files
                        // that no longer exist and entries the user removed
                        // on purpose (LibraryExclusions) — otherwise both
                        // kinds resurrected here on every launch. ResolveTrack
                        // registers the new track in _library itself.
                        t = ResolveTrack(p);
                        if (t == null)
                            continue;
                        byPath[p] = t;
                    }
                    pl.Tracks.Add(t);
                }
            }
            _playlists.Add(pl);
        }
    }

    // Chains background recent-list saves (same rationale as
    // PersistLibraryBackground: a synchronous serialize+replace on every
    // track change stuttered playback on the UI thread).
    private Task _recentSaveTask = Task.CompletedTask;

    private void PushRecent(Track track)
    {
        _recent.Remove(track);
        _recent.Insert(0, track);
        while (_recent.Count > 200)
            _recent.RemoveAt(_recent.Count - 1);

        var snapshot = _recent.ToList();
        _recentSaveTask = _recentSaveTask.ContinueWith(
            _ => PlaylistStore.SaveRecent(snapshot),
            TaskScheduler.Default);
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
            // slot, or stop if nothing is left. The slot is the current index
            // MINUS the removed entries that sat before it (each of those
            // shifted the survivor one step up) — the old plain `current`
            // skipped that many tracks whenever the batch also removed
            // earlier entries.
            if (list.Count > 0)
            {
                var slot = Math.Clamp(
                    current - indices.Count(i => i < current),
                    0, list.Count - 1);
                _playback.TakeOverAfterRemoval(slot);
            }
            else
            {
                _playback.Clear();
                ResetNowPlaying();
            }
            return;
        }

        // Any removal reshuffles albums/artists — invalidate the grids or the
        // removed entries lingered as ghosts until the next metadata reload.
        _groupCacheDirty = true;

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
        row.Background = FindResource("RowHover") as Brush ?? RowHoverBrush;
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

    /// <summary>Zero one track's play count (fixes stale/wrong counts).</summary>
    private void ClearPlayCount_Click(object sender, RoutedEventArgs e)
    {
        var track = _contextTrack ?? (sender as FrameworkElement)?.DataContext as Track;
        if (track == null)
            return;
        track.PlayCount = 0;
        PersistLibrary();
        PersistRecent(); // flush the zeroed count into recent.json too
        RefreshDisplay();
    }

    /// <summary>Zero the play counts of every selected track (multi-select).</summary>
    private void BtnBatchClearCount_Click(object sender, RoutedEventArgs e)
    {
        var sel = (_viewMode == "Grid" ? TrackGrid.SelectedItems : TrackList.SelectedItems)
            .Cast<Track>().ToList();
        if (sel.Count == 0)
            return;

        foreach (var t in sel)
            t.PlayCount = 0;
        PersistLibrary();
        PersistRecent(); // flush the zeroed counts into recent.json too
        RefreshDisplay();
        ShowInfoBar($"已清零 {sel.Count} 首歌曲的播放次数");
    }

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
            ReloadLyricsForCurrent();
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
            ReloadLyricsForCurrent();
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

        // Keep the bottom-bar heart in sync when the current track was toggled.
        if (ReferenceEquals(t, _currentTrack))
            UpdateBarFavIcon();

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

    // ListView containers are RECYCLED by default: as you scroll, the same
    // FontIcon gets rebound to different tracks and Loaded never re-fires.
    // When Loaded ran before the binding landed (fast scroll / bulk refresh),
    // the glyph stayed empty forever — the "missing favorite star" bug.
    private void FavIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
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
            // Suppress the recent-play entry the index event would push — the
            // track has not actually sounded, and this branch was polluting
            // 最近播放 / LastPlayed with never-played songs.
            _suppressNextRecent = true;
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

        ++_displayRefreshVersion;
        var filtered = FilterAndSortTracks(_activeTracks, _currentView, _albumFilter, _searchText, _sortBy);

        // One Reset instead of a Clear() followed by N Inserts — see
        // BulkObservableCollection. Search and sort rebuild this on every
        // keystroke, so the difference is noticeable on a large library.
        _displayTracks.ReplaceAll(filtered);

        TrackList.CanReorderItems = CanReorderPlaylist();

        UpdateEmptyHint();
        // Search filtering changes what the header subtitle should say.
        ContentSubtitle.Text = filtered.Count > 0 ? $"{filtered.Count} 首" : "";
    }

    private async Task RefreshDisplayAsync(bool scrollToTop)
    {
        if (_activeTracks == null)
            return;

        var version = ++_displayRefreshVersion;
        var snapshot = _activeTracks.ToList();
        var view = _currentView;
        var albumFilter = _albumFilter;
        var query = _searchText;
        var sortBy = _sortBy;
        var filtered = await Task.Run(() =>
            FilterAndSortTracks(snapshot, view, albumFilter, query, sortBy));

        if (version != _displayRefreshVersion)
            return;

        _displayTracks.ReplaceAll(filtered);
        TrackList.CanReorderItems = CanReorderPlaylist();
        UpdateEmptyHint();
        if (scrollToTop)
            ScrollTrackListToTop();
    }

    private static List<Track> FilterAndSortTracks(
        IEnumerable<Track> tracks,
        NavView view,
        string albumFilter,
        string query,
        string sortBy)
    {
        IEnumerable<Track> source = tracks;
        if (view == NavView.Albums && !string.IsNullOrEmpty(albumFilter))
        {
            source = source.Where(t =>
                string.Equals(t.Album, albumFilter, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = source.Where(t =>
            string.IsNullOrWhiteSpace(query)
            || (t.Title != null && t.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (t.Artist != null && t.Artist.Contains(query, StringComparison.OrdinalIgnoreCase))
            || (t.Album != null && t.Album.Contains(query, StringComparison.OrdinalIgnoreCase)));

        return sortBy switch
        {
            "Title" => filtered.OrderBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            "Artist" => filtered.OrderBy(t => t.Artist ?? "", StringComparer.OrdinalIgnoreCase)
                                 .ThenBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            "Album" => filtered.OrderBy(t => t.Album ?? "", StringComparer.OrdinalIgnoreCase)
                                .ThenBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            "DateAdded" => filtered.OrderByDescending(t => t.DateAdded).ToList(),
            "Duration" => filtered.OrderBy(t => t.Duration).ToList(),
            "PlayCount" => filtered.OrderByDescending(t => t.PlayCount)
                                   .ThenBy(t => t.Title ?? "", StringComparer.OrdinalIgnoreCase).ToList(),
            _ => filtered.ToList()
        };
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
        if (!_groupCacheDirty && _albumGridCache != null)
        {
            AlbumGrid.ItemsSource = _albumGridCache;
            return;
        }

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

        _albumGridCache = albums;
        _groupCacheDirty = false;
        AlbumGrid.ItemsSource = albums;
    }

    /// <summary>
    /// Build the artist grid: group library tracks by Artist name, create one
    /// <see cref="ArtistInfo"/> per group, and bind to <see cref="ArtistGrid"/>.
    /// </summary>
    private void BuildArtistGrid()
    {
        if (!_groupCacheDirty && _artistGridCache != null)
        {
            ArtistGrid.ItemsSource = _artistGridCache;
            return;
        }

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

        _artistGridCache = artists;
        _groupCacheDirty = false;
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
        {
            // 最近播放 hands the LIVE _recent list to the player. SetQueue's
            // index event immediately PushRecent()s the clicked track — which
            // INSERTS it at position 0 and shifts every index, so the player
            // loaded the PREVIOUS track (double-click row N played row N-1).
            // Snapshot it, same as the reordered branch above.
            if (ReferenceEquals(_activeTracks, _recent))
                StartPlay(_recent.ToList(), idx);
            else
                StartPlay(_activeTracks, idx);
        }
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
            // The queue is frequently the LIVE library itself (session
            // restore, plain library play). Drag-reordering or removing from
            // the queue panel then mutates the LIBRARY — without writing the
            // removal to LibraryExclusions or persisting — while a snapshot
            // queue should just reorder freely. Gate reordering by identity;
            // removal goes through the library path in QueueRemove_Click.
            QueueList.CanReorderItems = !ReferenceEquals(_playback.Queue, _library);
            QueueList.AllowDrop = QueueList.CanReorderItems;
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

        // Re-anchor the play counter to where the seek landed: without this,
        // a seek past the 30s threshold after a few seconds of listening
        // counted a "play" on the very next tick (the start position still
        // said 0s). Re-anchoring means the listener now owes the full
        // threshold from the new position. The scrobble start re-anchors too,
        // but a track that already scrobbled stays queued (no double report).
        _pendingCountStartSec = SeekSlider.Value;
        _pendingScrobbleStartSec = SeekSlider.Value;
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
        UpdateQueueBadge();
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
    private Microsoft.UI.Xaml.Media.Animation.Storyboard? _nowPlayingOutSb;

    /// <summary>Fade the immersive now-playing panel out, then collapse it.
    /// Collapsing immediately made panel exits feel clipped next to the
    /// animated entrance. Stop() before a re-entry cancels the pending
    /// collapse (a stopped storyboard never runs its Completed handler).</summary>
    private void AnimateNowPlayingOut()
    {
        if (NowPlayingPanel.Visibility != Visibility.Visible)
            return;
        _nowPlayingOutSb?.Stop();
        var fade = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(110)),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, NowPlayingPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            NowPlayingPanel.Visibility = Visibility.Collapsed;
            NowPlayingPanel.Opacity = 1;
        };
        _nowPlayingOutSb = sb;
        sb.Begin();
    }

    private static void AnimatePanelIn(FrameworkElement panel, CompositeTransform transform, double fromX, double fromY)
    {        transform.TranslateX = fromX;
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
        if (_playback.Queue is not IList<Track> q)
            return; // no queue — nothing to remove from

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
        if (_playback.Queue is not IList<Track> q)
            return; // no queue — nothing to remove from

        // When the queue IS the library, "remove from queue" means the same
        // thing to the user as "remove from library": go through the library
        // path (exclude + persist + refresh) instead of silently mutating the
        // live collection behind the UI's back.
        if (ReferenceEquals(q, _library))
        {
            RemoveFromLibraryPermanently(track);
            return;
        }

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

    /// <summary>
    /// Remove one track from the library the way the batch-remove path does:
    /// exclusion list, persistence, UI refresh. Used by the queue panel when
    /// the queue and the library are the same collection.
    /// </summary>
    private void RemoveFromLibraryPermanently(Track track)
    {
        var idx = _library.IndexOf(track);
        if (idx < 0)
            return;

        if (!_settings.LibraryExclusions.Contains(track.Path, StringComparer.OrdinalIgnoreCase))
            _settings.LibraryExclusions.Add(track.Path);
        SettingsStore.Save(_settings);

        _library.RemoveAt(idx);
        PersistLibrary();

        // The playing track itself: stop instead of silently continuing.
        if (_playback.Queue != null && _playback.CurrentIndex >= 0
            && _playback.CurrentIndex < _playback.Queue.Count
            && ReferenceEquals(_playback.Queue[_playback.CurrentIndex], track))
        {
            _playback.Clear();
            _boundQueue = null;
            QueueList.ItemsSource = null;
            ResetNowPlaying();
        }

        RefreshDisplay();
    }

    // ---------- Desktop lyrics ----------

    private void BtnDesktopLyrics_Checked(object sender, RoutedEventArgs e)
    {
        _desktopLyrics ??= new DesktopLyricsOverlay();
        _desktopLyrics.PositionReported += (x, y) => _dispatcher.TryEnqueue(() =>
        {
            _settings.LyricPosX = x;
            _settings.LyricPosY = y;
            _settings.LyricPosSaved = true;
            ScheduleSettingsSave();
        });
        _desktopLyrics.SizeReported += (w) => _dispatcher.TryEnqueue(() =>
        {
            _settings.LyricBoxWidth = w;
            ScheduleSettingsSave();
        });
        _settings.LyricOverlayEnabled = true;
        SettingsStore.Save(_settings);
        _desktopLyrics.ApplyStyle(_settings);
        _desktopLyrics.SetClickThrough(_settings.LyricClickThroughDefault);
        // Restore the position the user dragged it to last time (if any).
        if (_settings.LyricPosSaved)
            _desktopLyrics.SetPosition(_settings.LyricPosX, _settings.LyricPosY);
        _desktopLyrics.Activate();
        BtnClickThrough.IsEnabled = true;
        BtnClickThrough.IsChecked = _settings.LyricClickThroughDefault;

        if (_lyrics != null && _currentLineIndex >= 0)
        {
            var l = _lyrics.Lines[_currentLineIndex];
            _desktopLyrics.UpdateLyric(_currentTrack, l.Original ?? string.Empty, l.Romaji, l.Translation, 0);
        }
        else if (_currentTrack != null && _lyrics == null)
        {
            // Turning the overlay on while a track without lyrics is loaded.
            PushDesktopNoLyric(_currentTrack);
        }
    }

    /// <summary>Show "暂无歌词" on the desktop overlay for a track whose lyrics
    /// are missing. Updates the push-dedup snapshot so a later real line push
    /// still flows through normally.</summary>
    private void PushDesktopNoLyric(Track track)
    {
        if (_desktopLyrics == null)
            return;
        _pushedTrack = track;
        _pushedOriginal = "暂无歌词";
        _pushedRoma = null;
        _pushedTrans = null;
        _desktopLyrics.UpdateLyric(track, "暂无歌词", null, null, -1);
    }

    private void BtnDesktopLyrics_Unchecked(object sender, RoutedEventArgs e)
    {
        _settings.LyricOverlayEnabled = false;
        SettingsStore.Save(_settings);
        _desktopLyrics?.Close();
        _desktopLyrics = null;
        BtnClickThrough.IsEnabled = false;
        BtnClickThrough.IsChecked = false;
    }

    private void BtnClickThrough_Checked(object sender, RoutedEventArgs e)
    {
        _desktopLyrics?.SetClickThrough(true);
        // Persist: the bottom-bar toggle used to be lost on restart.
        _settings.LyricClickThroughDefault = true;
        SettingsStore.Save(_settings);
    }

    private void BtnClickThrough_Unchecked(object sender, RoutedEventArgs e)
    {
        _desktopLyrics?.SetClickThrough(false);
        _settings.LyricClickThroughDefault = false;
        SettingsStore.Save(_settings);
    }

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
            // Visual.RotationAngle is in RADIANS. 360f meant 57 rotations per
            // iteration; even 2π-per-second read as a frantic, uneven spin —
            // at 1.8 rev/s the 60 fps strobe makes the artwork appear to
            // change speed and direction (wagon-wheel effect). 2π over 32 s
            // = one calm turn every 32 s (11.25°/s).
            //
            // The easing function must be LINEAR explicitly: the default
            // keyframe interpolation eased out (uniform until ~75%, then
            // decelerating into each iteration boundary — the disc visibly
            // slowed, paused and snapped back to speed every turn).
            var linear = visual.Compositor.CreateLinearEasingFunction();
            spin.InsertKeyFrame(0f, 0f, linear);
            spin.InsertKeyFrame(1f, (float)(Math.PI * 2.0), linear);
            spin.Duration = TimeSpan.FromSeconds(32);
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
        BtnPlay.Content = new FontIcon { Glyph = _isPlaying ? "\uE103" : "\uE102", FontSize = 16 };
        UpdateDiscTimer();

        if (_isPlaying)
            _lyricTimer.Start();
        else
            _lyricTimer.Stop();

        // Freeze the desktop-lyrics sweep on pause: the overlay extrapolates
        // progress between samples and would keep gliding after the stream
        // stops. Send on both transitions so resume rebuilds cleanly.
        _desktopLyrics?.SetPaused(!_isPlaying);

        if (state == MediaPlaybackState.Playing)
        {
            _consecutiveFailures = 0;

            var idx = _playback.CurrentIndex;
            var q = _playback.Queue;
            if (q != null && idx >= 0 && idx < q.Count)
            {
                var t = q[idx];

                // Arm the play counter for this track, remembering where the
                // play STARTED. The count itself happens in OnPositionTick
                // once the track survives past the listen threshold — counting
                // at play-start made every random-mode skip and every
                // launch-resume inflate counts.
                if (!ReferenceEquals(t, _pendingCountTrack))
                {
                    _pendingCountTrack = t;
                    _pendingCountStartSec = _playback.Position.TotalSeconds;
                    _pendingScrobbleTrack = t;
                    _pendingScrobbleStartSec = _pendingCountStartSec;
                    _scrobbleQueued = false;
                }

                // Recent-play bookkeeping (skip suppressed during session restore).
                if (!ReferenceEquals(t, _lastRecentTrack) && !_suppressNextRecent)
                {
                    _lastRecentTrack = t;
                    PushRecent(t);
                }
                _suppressNextRecent = false;

                // Scrobble bookkeeping moved to OnPositionTick: eligibility now
                // requires that THIS play session started before the halfway
                // point, so resumed sessions (already past half) don't scrobble
                // after listening for a second.
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
        UpdateQueueBadge();
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

        // Play counting: a play happens when the track survives past the
        // listen threshold during THIS play session and the session started
        // before the threshold. Skips (a few seconds each) never get there;
        // resuming a session (starting past the threshold) never qualifies.
        // Threshold = 30s, capped at half the duration for very short files.
        if (_isPlaying && _pendingCountTrack != null)
        {
            var threshold = dur.TotalSeconds > 0 ? Math.Min(30, dur.TotalSeconds / 2) : 30;
            if (pos.TotalSeconds >= threshold && _pendingCountStartSec < threshold)
            {
                _pendingCountTrack.LastPlayed = DateTime.Now;
                _pendingCountTrack.PlayCount++;
                _libraryDirty = true;
                _pendingCountTrack = null; // counted — no re-count on pause/resume
            }
        }

        // Scrobble: like the play counter, only when this play session started
        // before the halfway point and has now crossed it. Resumed sessions
        // (starting past half) and sub-threshold skips never qualify. Tracked
        // independently of the play counter (which clears at 30s) and one-shot
        // via _scrobbleQueued — the old shared-reference version re-enqueued
        // the same track on every tick when resuming between 30s and half.
        if (_isPlaying && !_scrobbleQueued && dur.TotalSeconds > 30 && _pendingScrobbleTrack != null &&
            pos.TotalSeconds >= dur.TotalSeconds / 2.0 && _pendingScrobbleStartSec < dur.TotalSeconds / 2.0)
        {
            EnqueueScrobble(_pendingScrobbleTrack);
            _scrobbleQueued = true;
        }

        if ((DateTime.Now - _lastProgressSave).TotalSeconds >= 3)
        {
            _lastProgressSave = DateTime.Now;
            var progressIdx = _playback.CurrentIndex;
            var progressPos = pos;
            var progressPath = CurrentPath();
            Task.Run(() => PlaylistStore.SaveProgress(progressIdx, progressPos, progressPath));

            // Persist volume/rate alongside the 3s progress cadence instead of
            // only at clean exit (a killed process used to lose them).
            _settings.Volume = _playback.TargetVolume;
            _settings.PlaybackRate = _playback.Rate;
            ScheduleSettingsSave();

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
        UpdateBarFavIcon();
        _ = SampleCoverColorAsync(track.Cover);

        BindCurrentCover(track);

        // Parse OFF the UI thread: file probing + decode can stall for seconds
        // on network shares, and this runs on every track switch. The token
        // discards results superseded by a newer load or a ResetNowPlaying.
        var loadToken = ++_lyricsLoadToken;
        var parsePath = track.Path;
        var parseLyricPath = track.LyricPath;
        var parseEnc = _settings.LyricEncoding == "auto" ? null : _settings.LyricEncoding;
        _ = Task.Run(async () =>
        {
            LyricDocument? doc = null;
            try { doc = LyricsParser.Parse(parsePath, parseLyricPath, parseEnc); }
            catch { doc = null; }
            if (loadToken != _lyricsLoadToken) return; // superseded
            _dispatcher.TryEnqueue(() => ApplyLoadedLyrics(track, doc, loadToken));
        });
        return;
    }

    private void ApplyLoadedLyrics(Track track, LyricDocument? doc, int loadToken)
    {
        if (loadToken != _lyricsLoadToken) return; // superseded
        _lyrics = doc != null && doc.Lines.Count > 0 ? doc : null;
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
            // Mirror the state on the desktop overlay.
            PushDesktopNoLyric(track);
            return;
        }

        BuildLyricUI(_lyrics);

        // Highlight + center right away (e.g. session restore while paused)
        // instead of waiting for a position tick that may never arrive.
        UpdateLyricHighlight(_playback.Position);
    }

    private void BindCurrentCover(Track? track)
    {
        if (_currentTrack != null)
            _currentTrack.PropertyChanged -= CurrentTrack_PropertyChanged;

        _currentTrack = track;
        if (track != null)
            track.PropertyChanged += CurrentTrack_PropertyChanged;
    }

    private void CurrentTrack_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Track track)
            return;
        if (e.PropertyName == nameof(Track.Cover))
        {
            ApplyCover(track.Cover);
            MiniCover.Source = track.Cover;
            _ = SampleCoverColorAsync(track.Cover);
        }
        else if (e.PropertyName == nameof(Track.Favorite))
        {
            UpdateBarFavIcon();
        }
    }

    // Alternate between the two cover layers on every change, so switching
    // tracks crossfades instead of hard-cutting the artwork.
    private bool _coverFrontIsA = true;

    private void ApplyCover(ImageSource? cover)
    {
        var newBrush = cover == null
            ? CoverPlaceholder
            : new ImageBrush { ImageSource = cover };

        // EchoMusic rounded-square cover mirrors the artwork instantly.
        SquareCover.Background = newBrush;

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

    // ---------- EchoMusic look helpers ----------

    /// <summary>Square (EchoMusic default) vs vinyl disc (cover spin on).</summary>
    private void ApplyCoverMode()
    {
        bool vinyl = _settings.CoverSpin;
        CoverDisc.Visibility = vinyl ? Visibility.Visible : Visibility.Collapsed;
        SquareCover.Visibility = vinyl ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>While the immersive now-playing overlay is open the floating
    /// player bar turns into its dark translucent variant (EchoMusic's dark
    /// lyric-page transport), and back on close.</summary>
    private bool _bottomBarImmersive;

    /// <summary>Re-tints the floating player bar to the current theme. No-op
    /// while the immersive (fixed dark) variant is active.</summary>
    private void ApplyBottomBarThemeBrushes()
    {
        if (_bottomBarImmersive)
            return;
        PlayerBarBorder.Background = FindResource("BarGlass") as Microsoft.UI.Xaml.Media.Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.White);
        PlayerBarBorder.BorderBrush = FindResource("BorderSoft") as Microsoft.UI.Xaml.Media.Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void UpdateBottomBarImmersive(bool immersive)
    {
        if (_bottomBarImmersive == immersive)
            return;
        _bottomBarImmersive = immersive;
        BottomBarHost.RequestedTheme = immersive ? ElementTheme.Dark : ElementTheme.Default;
        if (immersive)
        {
            PlayerBarBorder.Background = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(0xB0, 0x14, 0x14, 0x18));
            PlayerBarBorder.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        else
        {
            ApplyBottomBarThemeBrushes();
        }
    }

    private void BtnExitNowPlaying_Click(object sender, RoutedEventArgs e)
        => ShowView(NavView.Local);

    /// <summary>Bottom-bar heart: toggle favourite on the current track.</summary>
    private void BtnBarFav_Click(object sender, RoutedEventArgs e)
    {
        if (_currentTrack == null)
            return;
        _currentTrack.Favorite = !_currentTrack.Favorite;
        _libraryDirty = true;
        PersistLibrary();
        UpdateBarFavIcon();

        if (_currentView == NavView.Favorites)
        {
            _activeTracks = _library.Where(tr => tr.Favorite).ToList();
            RefreshDisplay();
        }
    }

    private void UpdateBarFavIcon()
    {
        bool fav = _currentTrack?.Favorite ?? false;
        BarFavIcon.Glyph = fav ? "\uE735" : "\uE734";
        if (FindResource(fav ? "QqGreen" : "TextSecondary") is Microsoft.UI.Xaml.Media.Brush tint)
            BarFavIcon.Foreground = tint;
    }

    /// <summary>Count badge on the queue button (EchoMusic playlist bubble).</summary>
    private void UpdateQueueBadge()
    {
        int n = _playback.Queue?.Count ?? 0;
        QueueBadgeText.Text = n > 99 ? "99+" : n.ToString();
        QueueBadge.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"N 首" suffix next to the content title.</summary>
    private void UpdateContentSubtitle()
    {
        if (_currentView is NavView.Settings or NavView.LyricFill or NavView.NowPlaying)
        {
            ContentSubtitle.Text = "";
            return;
        }
        int n = _activeTracks?.Count ?? 0;
        ContentSubtitle.Text = n > 0 ? $"{n} 首" : "";
    }

    private void UpdateProfileSubtitle()
    {
        int n = _library?.Count ?? 0;
        ProfileSubtitle.Text = n > 0 ? $"本地曲库 · {n} 首" : "本地音乐库";
    }

    /// <summary>
    /// EchoMusic lyric-page ambience: a dark vertical gradient tinted by the
    /// artwork's dominant colour. The colour is sampled off the rendered mini
    /// cover (RenderTargetBitmap) — cheap, dependency-free, and works with
    /// every cover source the app already produces.
    /// </summary>
    private async Task SampleCoverColorAsync(ImageSource? cover)
    {
        Windows.UI.Color? tint = null;
        if (cover != null)
        {
            try
            {
                await Task.Delay(40); // let the mini cover complete a layout pass
                var rtb = new RenderTargetBitmap();
                await rtb.RenderAsync(MiniCover);
                if (rtb.PixelWidth > 0 && rtb.PixelHeight > 0)
                {
                    var buffer = await rtb.GetPixelsAsync();
                    var bytes = buffer.ToArray();
                    long r = 0, g = 0, b = 0; int used = 0;
                    long ar = 0, ag = 0, ab = 0; int all = 0;
                    int stride = 4;
                    for (int i = 0; i + 2 < bytes.Length; i += stride * 3) // sparse sampling
                    {
                        byte bl = bytes[i], gr = bytes[i + 1], rd = bytes[i + 2];
                        ar += rd; ag += gr; ab += bl; all++;
                        int max = Math.Max(rd, Math.Max(gr, bl));
                        int min = Math.Min(rd, Math.Min(gr, bl));
                        if (max - min > 26) // skip greys/near-whites so the accent hue survives
                        {
                            r += rd; g += gr; b += bl; used++;
                        }
                    }
                    if (used >= 8)
                        tint = Microsoft.UI.ColorHelper.FromArgb(255, (byte)(r / used), (byte)(g / used), (byte)(b / used));
                    else if (all > 0)
                        tint = Microsoft.UI.ColorHelper.FromArgb(255, (byte)(ar / all), (byte)(ag / all), (byte)(ab / all));
                }
            }
            catch
            {
                // Sampling is cosmetic; any failure just keeps the default ambience.
            }
        }
        ApplyNowOverlay(tint);
    }

    private void ApplyNowOverlay(Windows.UI.Color? tint)
    {
        // Semi-translucent ambience: the artwork tint rides ON TOP of the
        // window material (Mica / acrylic show through) instead of covering
        // it with an opaque slab. Bottom stays denser for lyric legibility.
        Windows.UI.Color top = tint.HasValue
            ? Microsoft.UI.ColorHelper.FromArgb(0xB4, (byte)(tint.Value.R * 55 / 100), (byte)(tint.Value.G * 55 / 100), (byte)(tint.Value.B * 55 / 100))
            : Microsoft.UI.ColorHelper.FromArgb(0xB4, 0x22, 0x1c, 0x22);
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Microsoft.UI.ColorHelper.FromArgb(0xE0, 0x0e, 0x0f, 0x14), Offset = 1 });
        NowPlayingPanel.Background = brush;
    }

    private void ResetNowPlaying()
    {
        SetCurrentTrackHighlight(null);
        // Full lyric/track state reset: leaving _loadedIndex/_lyrics behind let
        // later LoadLyricsFor(_loadedIndex) calls resurrect an unrelated
        // library track into the now-playing panel ("ghost").
        _loadedIndex = -1;
        _lyrics = null;
        _lyricsLoadToken++; // kill any in-flight parse
        _currentLineIndex = -1;
        _lyricPanels.Clear();
        LyricStack.Children.Clear();
        BindCurrentCover(null);
        NowTitle.Text = "未在播放";
        NowArtist.Text = string.Empty;
        NowAlbum.Text = string.Empty;
        ApplyCover(null);
        MiniCover.Source = null;
        MiniTitle.Text = "未在播放";
        MiniArtist.Text = string.Empty;
        UpdateBarFavIcon();
        TimeCurrent.Text = "00:00";
        TimeTotal.Text = "00:00";
        SeekSlider.Value = 0;
        // Nothing is playing, so the next play is a fresh entry even if it
        // happens to be the same track again.
        _lastRecentTrack = null;
        _pendingCountTrack = null;
    }

    private void BuildLyricUI(LyricDocument doc)
    {
        var showRoma = _settings.LyricShowRomaji;
        var showTrans = _settings.LyricShowTranslation;
        var order = LyricPreferences.ParseLineOrder(_settings.LyricLineOrder);

        _wordLines.Clear();
        _wordOverlays.Clear();

        foreach (var line in doc.Lines)
        {
            var panel = new StackPanel
            {
                Margin = new Thickness(0, 6, 0, 6),
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center
            };

            // Build each available line with its role, then append them in the
            // user's chosen order (same order the desktop overlay uses).
            // Word-timed originals get a horizontal two-stop gradient
            // (sung -> unsung) that UpdateWordHighlight slides karaoke-style.
            TextBlock? MakePart(char role)
            {
                switch (role)
                {
                    // Word-timed rows render via the karaoke Grid below —
                    // a static copy here would show the line TWICE.
                    case 'O' when line.Words is { Count: > 0 }:
                        return null;
                    case 'O' when !string.IsNullOrWhiteSpace(line.Original):
                        return MakeTextBlock(line.Original, 22, Microsoft.UI.Colors.White);
                    // Romaji/translation follow the desktop-lyric text color
                    // (the old hard-coded SkyBlue/LightGreen clashed with
                    // user themes).
                    case 'R' when showRoma && !string.IsNullOrWhiteSpace(line.Romaji):
                        return MakeTextBlock(line.Romaji, 14, ParseHex(_settings.LyricColor));
                    case 'T' when showTrans && !string.IsNullOrWhiteSpace(line.Translation):
                        return MakeTextBlock(line.Translation, 16, ParseHex(_settings.LyricColor));
                    default:
                        return null;
                }
            }

            // Assemble the row in the user's chosen order. A word-timed
            // original renders via the karaoke Grid appended afterwards, so
            // remember the slot where 'O' belongs and insert it there —
            // appending blindly put the original AFTER the romaji/translation
            // no matter what LyricLineOrder said.
            var originalSlot = -1;
            foreach (var role in order)
            {
                if (role == 'O')
                {
                    if (originalSlot < 0)
                        originalSlot = panel.Children.Count;
                    continue;
                }
                var tb = MakePart(role);
                if (tb != null)
                    panel.Children.Add(tb);
            }

            if (panel.Children.Count == 0)
                panel.Children.Add(MakeTextBlock("♪", 18, Microsoft.UI.Colors.Gray));

            LyricStack.Children.Add(panel);
            _lyricPanels.Add(panel);
            TextBlock? overlay = null;
            if (line.Words is { Count: > 0 })
            {
                // The karaoke row is a Grid: base copy in the unsung color +
                // an accent copy clipped to the sung prefix (slide per tick).
                var baseTb = MakeTextBlock(line.Original!, 22, ParseHex(_settings.LyricUnsungColor));
                overlay = MakeTextBlock(line.Original!, 22, ParseHex(_settings.LyricSungColor));
                // Start fully clipped: without a clip the sung-colour copy
                // shows through on every not-yet-sung line.
                overlay.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 0, 0) };
                var host = new Grid();
                host.Children.Add(baseTb);
                host.Children.Add(overlay);
                panel.Children.Insert(originalSlot < 0 ? panel.Children.Count : originalSlot, host);
            }
            _wordLines.Add(line);
            _wordOverlays.Add(overlay);
        }
    }

    /// <summary>Accent color for the karaoke sweep — follows the saved theme
    /// color so the sweep matches the rest of the UI.</summary>
    private static Windows.UI.Color AccentColor()
    {
        try
        {
            var s = (SettingsStore.Load().AccentColor ?? "#ef4444").TrimStart('#');
            if (s.Length == 6)
                return Microsoft.UI.ColorHelper.FromArgb(255,
                    Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch
        {
            // fall through to the default accent
        }
        return Microsoft.UI.ColorHelper.FromArgb(255, 0xef, 0x44, 0x44);
    }

    /// <summary>Rebuild the lyrics panel (e.g. after toggling romaji/translation)
    /// and re-highlight + re-push the current line to the desktop overlay.</summary>
    private void RebuildLyricUi()
    {
        LyricStack.Children.Clear();
        _lyricPanels.Clear();
        _wordLines.Clear();
        _wordOverlays.Clear();
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
            // (-1 == -1) happens right after a cold load (lyrics shown, playback
            // still at 0): nothing to highlight yet, and Lines[-1] would throw
            // ArgumentOutOfRangeException — which as a FailFast took the whole
            // app down ("crashed on startup / on adding a folder").
            if (idx < 0)
                return;
            var prog = CalcWordProgress(_lyrics.Lines[idx], t);
            UpdateWordHighlight(idx, t);
            PushDesktop(idx, prog);
            return;
        }

        if (_currentLineIndex >= 0 && _currentLineIndex < _lyricPanels.Count)
        {
            SetLineActive(_lyricPanels[_currentLineIndex], false);
            ResetWordHighlight(_currentLineIndex);
        }

        _currentLineIndex = idx;

        if (idx >= 0 && idx < _lyricPanels.Count)
        {
            SetLineActive(_lyricPanels[idx], true);
            var prog = CalcWordProgress(_lyrics.Lines[idx], t);
            UpdateWordHighlight(idx, t);
            PushDesktop(idx, prog);
            _lyricScrollTarget = _lyricPanels[idx];
            _dispatcher.TryEnqueue(ScrollLyricToCurrent);
        }

        PushDesktop(idx);
    }

    /// <summary>
    /// Continuous karaoke progress (0..1) of a word-timed line at time t —
    /// linear inside the current word so the sweep flows smoothly between
    /// word boundaries. -1 for lines without word timing.
    /// </summary>
    private double CalcWordProgress(Models.LyricLine line, TimeSpan t)
    {
        if (line.Words == null || line.Words.Count == 0)
            return -1;

        int totalChars = 0;
        double doneChars = 0;
        Models.LyricWord? current = null;
        foreach (var w in line.Words)
        {
            totalChars += w.Text.Length;
            if (w.Start <= t)
            {
                doneChars += w.Text.Length;
                current = w;
            }
        }

        if (totalChars == 0)
            return -1;
        if (current == null)
            return 0;

        var charsBefore = doneChars - current.Text.Length;
        var frac = current.Duration.TotalMilliseconds > 0
            ? Math.Clamp((t - current.Start).TotalMilliseconds / current.Duration.TotalMilliseconds, 0, 1)
            : 1;
        return (charsBefore + current.Text.Length * frac) / totalChars;
    }

    /// <summary>Characters sung so far in line <paramref name="idx"/> — also
    /// fed to the desktop overlay for its karaoke split.</summary>
    private int CountSungChars(Models.LyricLine line, TimeSpan t)
    {
        int sung = 0;
        if (line.Words == null)
            return -1;
        foreach (var w in line.Words)
        {
            if (w.Start <= t)
                sung += w.Text.Length;
            else
                break;
        }
        return sung;
    }

    /// <summary>
    /// Flowing karaoke sweep for a word-timed row: the accent overlay is
    /// clipped to the sung prefix (char count from the word timing), so the
    /// highlight washes over the line smoothly between word boundaries.
    /// </summary>
    private void UpdateWordHighlight(int idx, TimeSpan t)
    {
        if (idx < 0 || idx >= _wordOverlays.Count)
            return;
        var overlay = _wordOverlays[idx];
        var line = _wordLines[idx];
        if (overlay == null || line.Words == null || line.Words.Count == 0)
            return;

        var progress = CalcWordProgress(line, t);
        if (progress < 0)
            progress = 0;

        // Clip the accent copy to the sung prefix. ActualWidth is only valid
        // after layout — before that the clip stays zero (invisible), which is
        // exactly the "not sung yet" state.
        var wpx = overlay.ActualWidth;
        if (wpx > 0)
        {
            overlay.Clip = new RectangleGeometry
            {
                Rect = new Windows.Foundation.Rect(0, 0, wpx * progress, overlay.ActualHeight + 8)
            };
        }
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
    private double _pushedProgress = -1;

    private void PushDesktop(int idx, double progress = -1)
    {
        if (_desktopLyrics == null)
            return;
        if (idx < 0 || _lyrics == null)
        {
            // Before the first line (or no lyrics): clear the overlay instead
            // of letting the PREVIOUS track's last line linger on screen.
            if (_pushedTrack != null)
            {
                _desktopLyrics.UpdateLyric(_currentTrack, string.Empty, null, null, -1);
                _pushedTrack = null;
                _pushedOriginal = _pushedRoma = _pushedTrans = null;
            }
            return;
        }
        var line = _lyrics.Lines[idx];
        var roma = _settings.LyricShowRomaji ? line.Romaji : null;
        var trans = _settings.LyricShowTranslation ? line.Translation : null;
        var original = line.Original ?? string.Empty;

        // The track is part of the comparison too: switching songs must push
        // even when the first line happens to be identical.
        if (ReferenceEquals(_currentTrack, _pushedTrack)
            && string.Equals(original, _pushedOriginal, StringComparison.Ordinal)
            && string.Equals(roma, _pushedRoma, StringComparison.Ordinal)
            && string.Equals(trans, _pushedTrans, StringComparison.Ordinal)
            && Math.Abs(progress - _pushedProgress) < 0.0005)
            return;

        _pushedTrack = _currentTrack;
        _pushedOriginal = original;
        _pushedRoma = roma;
        _pushedTrans = trans;
        _pushedProgress = progress;
        _desktopLyrics.UpdateLyric(_currentTrack, original, roma, trans, progress);
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

    /// <summary>True when a word-timed lyric file (.qrc/.yrc/.krc) exists for
    /// the track's audio base name.</summary>
    private static bool HasWordLyricFile(Track t)
    {
        var basePath = Path.Combine(
            Path.GetDirectoryName(t.Path) ?? "",
            Path.GetFileNameWithoutExtension(t.Path));
        return File.Exists(basePath + ".qrc")
            || File.Exists(basePath + ".yrc")
            || File.Exists(basePath + ".krc");
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

        if (main != null) AtomicFile.WriteAllText(basePath + ".lrc", LyricsParser.RemoveBlankLines(main), utf8);
        if (zh != null) AtomicFile.WriteAllText(basePath + ".zh.lrc", LyricsParser.RemoveBlankLines(zh), utf8);
        if (ro != null) AtomicFile.WriteAllText(basePath + ".romaji.lrc", LyricsParser.RemoveBlankLines(ro), utf8);
        return true;
    }

    /// <summary>
    /// Save a WORD-TIMED main lyric (QRC/YRC/KRC) next to the audio, plus the
    /// line-level translation/romaji companions. Any same-base .lrc main file
    /// is removed: FindMainLyric prefers word-timed files anyway, and keeping
    /// both would let a stale .lrc shadow a future line-level re-download.
    /// </summary>
    private static bool SaveWordLyricFile(
        Track track, string content, string ext, string? trans, string? roma)
    {
        var basePath = Path.Combine(
            Path.GetDirectoryName(track.Path) ?? "",
            Path.GetFileNameWithoutExtension(track.Path));
        var utf8 = new System.Text.UTF8Encoding(false);

        try
        {
            AtomicFile.WriteAllText(basePath + ext, content, utf8);
            if (File.Exists(basePath + ".lrc"))
                File.Delete(basePath + ".lrc");
            if (!string.IsNullOrWhiteSpace(trans))
                AtomicFile.WriteAllText(basePath + ".zh.lrc", LyricsParser.RemoveBlankLines(trans), utf8);
            if (!string.IsNullOrWhiteSpace(roma))
                AtomicFile.WriteAllText(basePath + ".romaji.lrc", LyricsParser.RemoveBlankLines(roma), utf8);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task AutoDownloadLyricForTrackAsync(Track track)
    {
        ShowInfoBar($"正在为「{track.Title}」匹配歌词…");
        try
        {
            if (await TryAutoDownloadAsync(track))
            {
                if (_currentTrack == track)
                    ReloadLyricsForCurrent();
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
        bool tryKugou = sourceKind is LyricSourceKind.Auto or LyricSourceKind.KuGou;
        bool tryLrclib = sourceKind is LyricSourceKind.Auto or LyricSourceKind.LRCLIB;
        var fillMode = LyricPreferences.ParseFillMode(_settings.LyricFillMode);
        bool wordMode = _settings.LyricWordLyrics;

        // 0) Karaoke-first pass: when word-timed download is on, the word
        // sources are tried BEFORE the line-level ones — NetEase would almost
        // always win with a plain LRC and the karaoke option would never be
        // reached in Auto mode. Falls through to the normal chain on failure.
        if (wordMode)
        {
            if (tryQq)
            {
                var wordQqResults = await QQLyricService.SearchAsync(keyword, 20);
                var wordQqBest = BestMatch(wordQqResults, localTitle,
                    Norm(track.Artist == "未知歌手" ? "" : track.Artist), durationSec);
                if (wordQqBest != null)
                {
                    var raw = await QQLyricService.FetchLyricRawAsync(wordQqBest.SongMid, wordQqBest.SongId);
                    if (raw != null && !string.IsNullOrWhiteSpace(raw.Value.Lyric) &&
                        SaveWordLyricFile(track, raw.Value.Lyric!, ".qrc", raw.Value.Trans, raw.Value.Roma))
                        return true;
                }
            }

            if (tryKugou)
            {
                var wordKgResults = await KugouLyricService.SearchAsync(keyword, 20);
                var wordKgBest = BestMatch(wordKgResults, localTitle,
                    Norm(track.Artist == "未知歌手" ? "" : track.Artist), durationSec);
                if (wordKgBest != null)
                {
                    var (_, wordKrc) = await KugouLyricService.FetchLyricAsync(wordKgBest.SongMid);
                    if (!string.IsNullOrWhiteSpace(wordKrc) &&
                        SaveWordLyricFile(track, wordKrc!, ".krc", null, null))
                        return true;
                }
            }

            // Word-timed pass failed — the chain below saves plain LRC.
        }

        // 1) NetEase (full three-line set). Word-timed YRC needs the encrypted
        // eapi endpoint — the public one doesn't serve it — so NetEase stays
        // line-level for now.
        var neSong = tryNetEase ? await MatchNetEaseAsync(keyword, track.Title, durationSec) : null;
        if (neSong != null)
        {
            var ne = await NetEaseLyricService.FetchLyricAsync(neSong.SongMid);
            if (ne != null &&
                SaveLyricFiles(track, ne.Value.Lyric, ne.Value.Trans, ne.Value.Roma, fillMode))
                return true;
        }

        // 1b) KuGou (word-timed KRC + plain LRC, no login). Tried before QQ
        // because the public KRC endpoint works without cookies.
        if (tryKugou)
        {
            var kgResults = await KugouLyricService.SearchAsync(keyword, 20);
            var kgBest = BestMatch(kgResults, localTitle,
                Norm(track.Artist == "未知歌手" ? "" : track.Artist), durationSec);
            if (kgBest != null)
            {
                var (lrc, krc) = await KugouLyricService.FetchLyricAsync(kgBest.SongMid);
                if (wordMode && !string.IsNullOrWhiteSpace(krc) &&
                    SaveWordLyricFile(track, krc!, ".krc", null, null))
                    return true;
                if (!string.IsNullOrWhiteSpace(lrc) &&
                    SaveLyricFiles(track, lrc, null, null, fillMode))
                    return true;
            }
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
                if (wordMode)
                {
                    // Karaoke mode: keep the raw QRC word timing, save as .qrc.
                    var raw = await QQLyricService.FetchLyricRawAsync(best.SongMid, best.SongId);
                    if (raw != null && !string.IsNullOrWhiteSpace(raw.Value.Lyric) &&
                        SaveWordLyricFile(track, raw.Value.Lyric!, ".qrc", raw.Value.Trans, raw.Value.Roma))
                        return true;
                }

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

    /// <summary>
    /// Shared match scoring for lyric search results: loose title match plus
    /// artist bonus and duration proximity. Same rules every source branch
    /// used to duplicate inline.
    /// </summary>
    private static QQSong? BestMatch(List<QQSong> results, string localTitle, string primaryArtist, int? durationSec)
    {
        QQSong? best = null;
        var bestScore = int.MinValue;
        var nt = Norm(localTitle);
        if (nt.Length == 0)
            return null;

        foreach (var r in results)
        {
            var rt = Norm(r.Title);
            if (rt.Length == 0)
                continue;

            int score;
            if (rt == nt)
                score = 10;
            else if (rt.Contains(nt, StringComparison.Ordinal) ||
                     nt.Contains(rt, StringComparison.Ordinal))
                score = 6;
            else
                continue;

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

        return bestScore >= 10 ? best : null;
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
        var win = new OnlineLyricWindow(_settings, BuildSearchKeyword(track),
            WinRT.Interop.WindowNative.GetWindowHandle(this));
        win.Activate();
        var picked = await win.Completion;
        if (picked == null)
            return false;
        var selected = picked.Value.Song;
        var source = picked.Value.SourceIndex;
        var sourceName = source switch { 1 => "网易云", 2 => "LRCLIB", 3 => "酷狗", _ => "QQ音乐" };

        ShowInfoBar($"正在下载歌词（{sourceName}）：{selected.Title} - {selected.Artist}");
        (string? Lyric, string? Trans, string? Roma)? lyric;
        bool wordModeForManual = _settings.LyricWordLyrics;
        string? krcContent = null;
        try
        {
            if (source == 3)
            {
                var kg = await KugouLyricService.FetchLyricAsync(selected.SongMid);
                krcContent = kg.Krc;
                lyric = (kg.Lrc, null, null);
            }
            else
            {
                lyric = source switch
                {
                    1 => await NetEaseLyricService.FetchLyricAsync(selected.SongMid),
                    2 => await LrclibService.FetchLyricAsync(selected.SongMid),
                    _ => await QQLyricService.FetchLyricAsync(selected.SongMid, selected.SongId),
                };
            }
        }
        catch (Exception ex)
        {
            ShowInfoBar($"下载歌词失败：{ex.Message}");
            return false;
        }
        if (string.IsNullOrEmpty(lyric?.Lyric))
        {
            ShowInfoBar("该歌曲没有可用歌词。");
            return false;
        }

        // QQ karaoke: keep the raw QRC word timing when enabled.
        if (source == 0 && wordModeForManual && !string.IsNullOrWhiteSpace(lyric?.Lyric) &&
            !string.IsNullOrWhiteSpace(selected.SongId))
        {
            var rawQq = await QQLyricService.FetchLyricRawAsync(selected.SongMid, selected.SongId);
            if (rawQq != null && !string.IsNullOrWhiteSpace(rawQq.Value.Lyric) &&
                SaveWordLyricFile(track, rawQq.Value.Lyric!, ".qrc", rawQq.Value.Trans, rawQq.Value.Roma))
            {
                if (_currentTrack == track)
                    ReloadLyricsForCurrent();
                ShowInfoBar($"已保存逐字歌词：{selected.Title} - {selected.Artist}");
                return true;
            }
        }

        // KuGou carries its own word-timed KRC: in karaoke mode save that
        // instead of the plain LRC tuple above.
        if (source == 3 && !string.IsNullOrWhiteSpace(lyric?.Lyric))
        {
            var fillMode = LyricPreferences.ParseFillMode(_settings.LyricFillMode);
            if (wordModeForManual && !string.IsNullOrWhiteSpace(krcContent) &&
                SaveWordLyricFile(track, krcContent!, ".krc", null, null))
            {
                if (_currentTrack == track)
                    ReloadLyricsForCurrent();
                ShowInfoBar($"已保存逐字歌词：{selected.Title} - {selected.Artist}");
                return true;
            }
            if (SaveLyricFiles(track, lyric.Value.Lyric, null, null, fillMode))
            {
                if (_currentTrack == track)
                    ReloadLyricsForCurrent();
                ShowInfoBar($"已保存歌词：{selected.Title} - {selected.Artist}");
                return true;
            }
            return false;
        }

        string? extraNote = null;
        try
        {
            // Drop an old manual binding only AFTER the new file actually
            // saved: LyricBindingStore wins over auto-detection on restart,
            // so clearing it before the write would leave the user with NO
            // lyrics at all when the save fails (read-only folder, ...).
            track.LyricPath = null;
            LyricBindingStore.Clear(track.Path);

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
            ReloadLyricsForCurrent();
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
    private (List<Track> NoLyric, List<Track> Incomplete) ClassifyLyricTracks(List<Track> tracks)
    {
        var noLyric = new List<Track>();
        var incomplete = new List<Track>();
        var forcedEnc = _settings.LyricEncoding == "auto" ? null : _settings.LyricEncoding;

        var wordMode = _settings.LyricWordLyrics;

        foreach (var t in tracks)
        {
            try
            {
                if (!HasLocalLyric(t))
                {
                    noLyric.Add(t);
                    continue;
                }

                // Karaoke mode upgrade: a track that only has a line-level
                // lyric (plain .lrc/.srt) is re-downloaded as word-timed
                // (.qrc/.krc) — otherwise "补全歌词" would never upgrade an
                // existing library to karaoke.
                if (wordMode && !HasWordLyricFile(t))
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
            catch
            {
                // A vanished/locked lyric file (TOCTOU between HasLocalLyric's
                // File.Exists and the actual read) must not escape into the
                // async-void caller as an unhandled exception. Treat as
                // "no lyric" so the download path can rebuild it.
                noLyric.Add(t);
            }
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
        // Flag BEFORE the first await: the multi-second scan used to run with
        // the button still enabled, letting a second click start a concurrent
        // run that cleared this queue mid-flight.
        _batchLyricRunning = true;
        UpdateLyricSummary();

        // Snapshot on the UI thread: the worker enumerates the collection while
        // the user can edit it.
        var snapshot = _activeTracks.ToList();

        // Classification stats every track on disk (File.Exists checks plus a
        // full parse of each lyric file), which is seconds of blocking I/O on a
        // large list — run it off the UI thread so the window stays responsive.
        LyricStatusText.Text = "正在扫描曲库…";
        List<Track> noLyric, incomplete;
        try
        {
            (noLyric, incomplete) = await Task.Run(() => ClassifyLyricTracks(snapshot));
        }
        catch (Exception ex)
        {
            // The scan reads every lyric file; one locked/vanished file used to
            // escape as an unhandled async-void exception (app "crash" dialog)
            // AND left _batchLyricRunning stuck, dead-locking the start button.
            _batchLyricRunning = false;
            UpdateLyricSummary();
            LyricStatusText.Text = $"扫描歌词失败：{ex.Message}";
            return;
        }

        if (noLyric.Count == 0 && incomplete.Count == 0)
        {
            // Early-out used to leave _batchLyricRunning set — the start
            // button stayed disabled until the app was restarted.
            _batchLyricRunning = false;
            UpdateLyricSummary();
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
                catch (ObjectDisposedException) { } // CTS disposed by a stop race
            }

            if (refreshCurrent)
                ReloadLyricsForCurrent();

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
        catch (OperationCanceledException)
        {
            // Cancellation is expected when the window closes or an operation
            // is explicitly stopped; do not surface it as a user-facing error.
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
            else if (child is Grid karaoke)
            {
                // Word-timed originals live in a Grid (unsung base + sung
                // overlay); without this branch the original line never took
                // part in the active-line dim/brighten.
                karaoke.Opacity = active ? 1.0 : 0.45;
                karaoke.Scale = active
                    ? new System.Numerics.Vector3(1.03f, 1.03f, 1f)
                    : System.Numerics.Vector3.One;
                foreach (var inner in karaoke.Children)
                    if (inner is TextBlock ktb)
                        ktb.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
    }

    /// <summary>Clear the karaoke clip of a line we are leaving — a seek back
    /// used to leave already-sung lines permanently tinted with the sung
    /// colour because their overlay clip was never reset.</summary>
    private void ResetWordHighlight(int idx)
    {
        if (idx < 0 || idx >= _wordOverlays.Count)
            return;
        var overlay = _wordOverlays[idx];
        if (overlay != null)
            overlay.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, 0, 0) };
    }

    // ---------- Settings ----------

    private string _currentSettingsTab = "look";

    /// <summary>EchoMusic-style settings tabs: show only the cards that
    /// belong to the selected section and underline the active tab. Newly
    /// revealed cards fade in (suppressed for theme re-application, which
    /// calls this with animate:false to avoid replaying the motion).</summary>
    private void ShowSettingsTab(string tab, bool animate = true)
    {
        _currentSettingsTab = tab;
        void Show(bool on, params FrameworkElement[] els)
        {
            foreach (var el in els)
            {
                bool was = el.Visibility == Visibility.Visible;
                el.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                if (on && !was && animate)
                {
                    var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(140)) };
                    Storyboard.SetTarget(fadeIn, el);
                    Storyboard.SetTargetProperty(fadeIn, "Opacity");
                    var sb = new Storyboard();
                    sb.Children.Add(fadeIn);
                    sb.Begin();
                }
            }
        }
        Show(tab == "look", SettingsCardLook);
        Show(tab == "lyric", SettingsCardLyric);
        Show(tab == "play", SettingsCardPlay, SettingsCardClose, SettingsCardHotkeys, SettingsCardSystem);
        Show(tab == "data", SettingsCardData, SettingsCardLastFm);

        void Mark(Button b, bool on)
        {
            // Unselected tabs ClearValue so their colour comes from the style's
            // {ThemeResource TextPrimary} and follows theme switches; a plain
            // code-assigned brush would keep the old theme's colour.
            if (on)
            {
                if (FindResource("QqGreen") is Microsoft.UI.Xaml.Media.Brush accent)
                {
                    b.Foreground = accent;
                    b.BorderBrush = accent;
                }
            }
            else
            {
                b.ClearValue(Microsoft.UI.Xaml.Controls.Control.ForegroundProperty);
                b.ClearValue(Microsoft.UI.Xaml.Controls.Control.BorderBrushProperty);
            }
        }
        Mark(SettingsTabLook, tab == "look");
        Mark(SettingsTabLyric, tab == "lyric");
        Mark(SettingsTabPlay, tab == "play");
        Mark(SettingsTabData, tab == "data");
    }

    private void SettingsTab_Click(object sender, RoutedEventArgs e)
        => ShowSettingsTab((string)((FrameworkElement)sender).Tag);

    private void ShowSettings()
    {
        // Every open starts at the top — the ScrollViewer keeps its old pixel
        // offset from the previous visit otherwise.
        ShowSettingsTab("look");
        _dispatcher.TryEnqueue(() => SettingsScroll.ChangeView(null, 0, null, true));

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
        SungColorPicker.Color = ParseHex(_settings.LyricSungColor);
        UnsungColorPicker.Color = ParseHex(_settings.LyricUnsungColor);
        UpdateColorSwatches();
        var material = _settings.WindowMaterial;
        MaterialMica.IsChecked = material == "Mica";
        MaterialMicaAlt.IsChecked = material == "MicaAlt";
        MaterialAcrylic.IsChecked = material is not ("Mica" or "MicaAlt" or "AcrylicThin");
        MaterialAcrylicThin.IsChecked = material == "AcrylicThin";
        LyricOpacitySlider.Value = _settings.LyricBgOpacity * 100;
        LyricBoldToggle.IsOn = _settings.LyricBold;
        LyricAlignCombo.SelectedIndex = _settings.LyricAlign == "Left" ? 1 : 0;
        LyricLineOrderCombo.SelectedIndex = LyricPreferences.ParseLineOrder(_settings.LyricLineOrder) switch
        {
            "OTR" => 1,
            "ROT" => 2,
            "TOR" => 3,
            "RTO" => 4,
            "TRO" => 5,
            _ => 0,
        };
        LyricClickThroughToggle.IsOn = _settings.LyricClickThroughDefault;
        LyricVerticalToggle.IsOn = _settings.LyricVertical;
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

        AccentColorPicker.Color = ParseHex(string.IsNullOrEmpty(_settings.AccentColor) ? "#ef4444" : _settings.AccentColor);
        UpdateColorSwatches();

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
        if (_suppressSettingEvents)
            return;
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
            ReloadLyricsForCurrent();
    }

    private void WordLyricsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents) return; // initialization assignment
        _settings.LyricWordLyrics = WordLyricsToggle.IsOn;
        SettingsStore.Save(_settings);
    }

    private void LyricSourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents) return; // initialization assignment

        _settings.LyricSource = LyricPreferences.ToSetting(LyricSourceCombo.SelectedIndex switch
        {
            1 => LyricSourceKind.NetEase,
            2 => LyricSourceKind.QQ,
            3 => LyricSourceKind.KuGou,
            4 => LyricSourceKind.LRCLIB,
            _ => LyricSourceKind.Auto
        });
        SettingsStore.Save(_settings);
        SyncLyricOptionChecks();
    }

    private void LyricFillModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents) return; // initialization assignment

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

        WordLyricsToggle.IsOn = _settings.LyricWordLyrics;
        LyricSourceCombo.SelectedIndex = src switch
        {
            LyricSourceKind.NetEase => 1,
            LyricSourceKind.QQ => 2,
            LyricSourceKind.KuGou => 3, LyricSourceKind.LRCLIB => 4,
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
        if (_suppressSettingEvents) return; // initialization assignment

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
            "randombag.json", "lastfm_pending.json", "loudness.json", "metacache.json",
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
        UpdateColorSwatches();
        if (_suppressSettingEvents)
            return;
        _settings.AccentColor = ToHex(args.NewColor);
        ScheduleSettingsSave();
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

    /// <summary>Apply the persisted window material (tubatools-style four
    /// variants) to this window.</summary>
    private void ApplyWindowMaterial()
    {
        var kind = _settings.WindowMaterial switch
        {
            "Mica" => WindowMaterialKind.Mica,
            "MicaAlt" => WindowMaterialKind.MicaAlt,
            "AcrylicThin" => WindowMaterialKind.AcrylicThin,
            _ => WindowMaterialKind.Acrylic,
        };
        WindowMaterialService.Apply(this, kind);
    }

    private bool _materialReappliedOnActivate;

    private void OnFirstActivatedReapplyMaterial(object sender, WindowActivatedEventArgs e)
    {
        if (_materialReappliedOnActivate || e.WindowActivationState == WindowActivationState.Deactivated)
            return;
        _materialReappliedOnActivate = true;
        Activated -= OnFirstActivatedReapplyMaterial;
        ApplyWindowMaterial();
    }

    private void WindowMaterial_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents)
            return;
        if (sender is RadioButton rb && rb.Tag is string tag)
        {
            _settings.WindowMaterial = tag;
            SettingsStore.Save(_settings);
            ApplyWindowMaterial();
        }
    }

    /// <summary>Switches between the dark and light resource dictionaries. Setting
    /// RequestedTheme on the root is what makes every {ThemeResource} in
    /// Themes/SukiTheme.xaml re-resolve, so nothing needs repainting by hand.</summary>
    private void ApplyThemeMode()
    {
        var light = string.Equals(_settings.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase);
        RootGrid.RequestedTheme = light ? ElementTheme.Light : ElementTheme.Dark;

        // Feed the track-title converter the theme-aware default foreground.
        CurrentTrackBrushConverter.Fallback = FindResource("TextPrimary") as Microsoft.UI.Xaml.Media.Brush;
        ApplyTitleBarTheme(light);

        // On a live switch, redo the brushes that were assigned from code: unlike
        // {ThemeResource} they are plain property values and won't re-resolve.
        // Skipped on the first pass — the tree has no DataContext yet and the
        // initial ShowView / Loaded handlers set these up correctly anyway.
        if (_themeModeApplied)
        {
            SetNavSelected(_currentView);
            // Only touch the favourite stars — a blanket sweep would also
            // rewrite the row "play next" glyph (it binds a Track as well).
            ForEachVisual<FontIcon>(RootGrid, icon =>
            {
                if ((icon.Name == "CardFavIcon" || icon.Name == "RowFavIcon")
                    && icon.DataContext is Track t)
                    UpdateFavIcon(icon, t.Favorite);
            });
            if (FindResource("TextSecondary") is Microsoft.UI.Xaml.Media.Brush dim)
                CacheDirStatus.Foreground = dim;
            foreach (var item in _lyricTasks) StampLyricTaskBrush(item);
            // The floating player bar's background is assigned from code, so
            // it must be re-tinted on a theme switch (only the immersive
            // variant is theme-independent).
            ApplyBottomBarThemeBrushes();
            UpdateSoundFxIcon();
            if (_currentView == NavView.Settings)
                ShowSettingsTab(_currentSettingsTab, animate: false);
            // Rebind the visible lists so the title converter re-evaluates
            // against the new Fallback brush.
            RefreshDisplay();
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
        var color = ParseHex(string.IsNullOrEmpty(_settings.AccentColor) ? "#ef4444" : _settings.AccentColor);
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
        ScheduleSettingsSave();
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
        UpdateColorSwatches();
        if (_suppressSettingEvents) return; // initialization assignment
        _settings.LyricColor = ToHex(args.NewColor);
        ScheduleSettingsSave();
        ApplyStyleLive();
    }

    /// <summary>Mirrors the four colour pickers onto their sidebar swatch
    /// buttons (the pickers themselves live inside flyouts).</summary>
    private void UpdateColorSwatches()
    {
        void Tint(Border? swatch, Windows.UI.Color c)
        {
            if (swatch != null)
                swatch.Background = new SolidColorBrush(c);
        }
        Tint(AccentSwatch, AccentColorPicker.Color);
        Tint(LyricColorSwatch, LyricColorPicker.Color);
        Tint(SungSwatch, SungColorPicker.Color);
        Tint(UnsungSwatch, UnsungColorPicker.Color);
    }

    private void SungColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        UpdateColorSwatches();
        if (_suppressSettingEvents) return; // initialization assignment
        _settings.LyricSungColor = ToHex(args.NewColor);
        ScheduleSettingsSave();
        ApplyStyleLive();
    }

    private void UnsungColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        UpdateColorSwatches();
        if (_suppressSettingEvents) return; // initialization assignment
        _settings.LyricUnsungColor = ToHex(args.NewColor);
        ScheduleSettingsSave();
        ApplyStyleLive();
    }

    private void LyricOpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_suppressSettingEvents)
            return;
        _settings.LyricBgOpacity = e.NewValue / 100.0;
        ScheduleSettingsSave();
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
        if (_suppressSettingEvents)
            return;
        _settings.LyricAlign = LyricAlignCombo.SelectedIndex == 1 ? "Left" : "Center";
        SettingsStore.Save(_settings);
        ApplyStyleLive();
    }

    private void LyricLineOrderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSettingEvents)
            return;
        _settings.LyricLineOrder = LyricLineOrderCombo.SelectedIndex switch
        {
            1 => "OTR",
            2 => "ROT",
            3 => "TOR",
            4 => "RTO",
            5 => "TRO",
            _ => "ORT",
        };
        SettingsStore.Save(_settings);
        // Reorder the in-app lyrics panel and push the new order to the overlay.
        RebuildLyricUi();
        ApplyStyleLive();
    }

    private void LyricClickThroughToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.LyricClickThroughDefault = ((ToggleSwitch)sender).IsOn;
        SettingsStore.Save(_settings);
        // Apply live to an open overlay — the bottom-bar toggle does the same;
        // the settings copy used to only write the default for next time.
        if (_desktopLyrics != null)
            _desktopLyrics.SetClickThrough(_settings.LyricClickThroughDefault);
    }

    private void LyricVerticalToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppressSettingEvents)
            return;
        _settings.LyricVertical = LyricVerticalToggle.IsOn;
        SettingsStore.Save(_settings);
        ApplyStyleLive();
    }

    private void CoverSpinToggle_Toggled(object sender, RoutedEventArgs e)
    {
        _settings.CoverSpin = ((ToggleSwitch)sender).IsOn;
        SettingsStore.Save(_settings);
        ApplyCoverMode();
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
        ScheduleSettingsSave();
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

    /// <summary>
    /// Coalesce settings.json writes from hot paths (picker drags, slider
    /// ticks, overlay drag reports): one flush 500ms after the last change
    /// instead of a synchronous serialize+replace per event.
    /// </summary>
    private void ScheduleSettingsSave()
    {
        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    /// <summary>
    /// The sound-effect window calls this after any knob changes. Coalesced:
    /// the current track is re-loaded (resuming at the same position) once the
    /// user stops adjusting, because the FFmpeg filter chain is baked into the
    /// media source at load time.
    /// </summary>
    public void ApplySoundFxDebounced()
    {
        _soundFxApplyTimer.Stop();
        _soundFxApplyTimer.Start();
    }

    private void OnSoundFxApplyTick(object? sender, object e)
    {
        _soundFxApplyTimer.Stop();
        if (_playback.CurrentIndex >= 0)
            _playback.ReloadCurrent();
    }

    /// <summary>Open the sound-effect panel (centered on this window).</summary>
    private void SoundFxOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_soundFxWindow != null)
        {
            _soundFxWindow.Activate();
            return;
        }

        _soundFxWindow = new SoundEffectWindow(_settings,
            WinRT.Interop.WindowNative.GetWindowHandle(this), ApplySoundFxDebounced);
        _soundFxWindow.Closed += (_, _) =>
        {
            _soundFxWindow = null;
            UpdateSoundFxIcon(); // the panel persists SoundEffectsEnabled on close
        };
        _soundFxWindow.Activate();
    }

    /// <summary>Lights the player-bar sound-fx button while effects are on.</summary>
    private void UpdateSoundFxIcon()
    {
        bool on = _settings.SoundEffectsEnabled;
        SoundFxIcon.Foreground = on
            ? FindResource("QqGreen") as Microsoft.UI.Xaml.Media.Brush ?? SoundFxIcon.Foreground
            : FindResource("TextSecondary") as Microsoft.UI.Xaml.Media.Brush ?? SoundFxIcon.Foreground;
    }

    private SoundEffectWindow? _soundFxWindow;

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
        if (_suppressSettingEvents) return; // initialization assignment

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

        // WindowPosSaved (not "coordinate >= 0") is the sentinel: windows on
        // monitors LEFT OF / ABOVE the primary screen legitimately have
        // negative coordinates, which used to reset their saved position.
        int x = _settings.WindowPosSaved ? _settings.WindowX : vx + (vw - w) / 2;
        int y = _settings.WindowPosSaved ? _settings.WindowY : vy + (vh - h) / 2;
        x = Math.Max(vx, Math.Min(x, vx + vw - w));
        y = Math.Max(vy, Math.Min(y, vy + vh - h));

        NativeMethods.MoveWindow(hwnd, x, y, w, h, true);
    }

    private void MainWindow_Closed(object? sender, WindowEventArgs e)
    {
        // Scrobbles sitting in the 2s debounce window (or mid-flight batch)
        // used to be dropped on exit.
        FlushScrobbleQueue();
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

        _metadataCts.Cancel();
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

        // Persist the random round so it survives restarts (random mode only).
        if (_playback.Mode == PlayMode.Random)
            PlaylistStore.SaveRandomBag(_playback.RandomBagRemaining.Select(t => t.Path));
        else
            PlaylistStore.ClearRandomBag();

        // Synchronous, unlike the periodic saves which run in the background: a
        // pending background save may not have finished by now, and PlayCount /
        // Favorite changes would be lost with it.
        try
        {
            _librarySaveTask.GetAwaiter().GetResult();
        }
        catch
        {
            // The synchronous final save below is still attempted.
        }
        PersistLibrary();

        // Persist window geometry + volume.
        var hwnd = WindowNative.GetWindowHandle(this);
        if (NativeMethods.GetWindowRect(hwnd, out var r))
        {
            _settings.WindowW = r.Width;
            _settings.WindowH = r.Height;
            _settings.WindowX = r.Left;
            _settings.WindowY = r.Top;
            _settings.WindowPosSaved = true;
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
