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

        // Restore persisted volume (so it matches the last session).
        _playback.Volume = _settings.Volume;
        VolumeSlider.Value = _settings.Volume * 100.0;
        SeekSlider.Maximum = 1;

        // Apply the persisted theme color before the first paint.
        ApplyAccentColor();

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

        this.Activated += MainWindow_Activated;
        this.Closed += MainWindow_Closed;

        RestoreSession();
        ShowView(NavView.Local);

        // Apply the embedded app icon to the window title bar / taskbar.
        TrySetWindowIcon();
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
                ViewCombo.Visibility = Visibility.Visible;
                SortCombo.Visibility = Visibility.Visible;
                break;
            case "recent":
                BtnClearRecent.Visibility = Visibility.Visible;
                ViewCombo.Visibility = Visibility.Visible;
                SortCombo.Visibility = Visibility.Visible;
                break;
            case "playlist":
                BtnPlayPlaylist.Visibility = Visibility.Visible;
                BtnAddToPlaylist.Visibility = Visibility.Visible;
                BtnDeletePlaylist.Visibility = Visibility.Visible;
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
                    nameBox
                }
            }
        };

        dialog.PrimaryButtonClick += (_, _) =>
        {
            var name = (nameBox.Text ?? string.Empty).Trim();
            if (name.Length == 0)
                return;

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

    private void BtnDeletePlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (_currentPlaylist == null)
            return;
        _playlists.Remove(_currentPlaylist);
        _currentPlaylist = null;
        PersistPlaylists();
        ShowView(NavView.Local);
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

    private void TrackGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
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

        UpdateEmptyHint();
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

    private void SeekSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isSeeking = true;
        SeekSlider.CapturePointer(e.Pointer);
    }

    private void SeekSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isSeeking)
            return;
        _isSeeking = false;
        if (SeekSlider.PointerCaptures.Count > 0)
            SeekSlider.ReleasePointerCapture(e.Pointer);
        _playback.Seek(TimeSpan.FromSeconds(SeekSlider.Value));
    }

    private void SeekSlider_PointerCanceled(object sender, PointerRoutedEventArgs e) => _isSeeking = false;

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
        if (e.Key == Windows.System.VirtualKey.Space)
        {
            BtnPlay_Click(sender, e);
            e.Handled = true;
        }
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
        _desktopLyrics.ApplyStyle(_settings);
        _desktopLyrics.SetClickThrough(_settings.LyricClickThroughDefault);
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
    }

    private void OnCurrentIndexChanged(int index)
    {
        var q = _playback.Queue;
        if (q != null && index >= 0 && index < q.Count)
        {
            var t = q[index];
            t.LastPlayed = DateTime.Now;
            PushRecent(t);
        }

        LoadLyricsFor(index);

        QueueList.SelectedIndex = _playback.CurrentIndex;
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

        _lyrics = LyricsParser.Parse(track.Path, track.LyricPath);
        if (_lyrics == null || _lyrics.Lines.Count == 0)
        {
            LyricStack.Children.Add(new TextBlock
            {
                Text = "暂无歌词",
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Gray),
                HorizontalAlignment = HorizontalAlignment.Center,
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
            if (!string.IsNullOrWhiteSpace(line.Romaji))
                panel.Children.Add(MakeTextBlock(line.Romaji, 14, Microsoft.UI.Colors.SkyBlue));
            if (!string.IsNullOrWhiteSpace(line.Translation))
                panel.Children.Add(MakeTextBlock(line.Translation, 16, Microsoft.UI.Colors.LightGreen));

            if (panel.Children.Count == 0)
                panel.Children.Add(MakeTextBlock("♪", 18, Microsoft.UI.Colors.Gray));

            LyricStack.Children.Add(panel);
            _lyricPanels.Add(panel);
        }
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

        var idx = -1;
        for (var i = 0; i < _lyrics.Lines.Count; i++)
        {
            if (_lyrics.Lines[i].Time <= pos)
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
        _desktopLyrics.UpdateLyric(_currentTrack, line.Original ?? string.Empty, line.Romaji, line.Translation);
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
        LyricColorPicker.Color = ParseHex(_settings.LyricColor);
        LyricOpacitySlider.Value = _settings.LyricBgOpacity * 100;
        LyricBoldToggle.IsOn = _settings.LyricBold;
        LyricAlignCombo.SelectedIndex = _settings.LyricAlign == "Left" ? 1 : 0;
        LyricClickThroughToggle.IsOn = _settings.LyricClickThroughDefault;
        CoverSpinToggle.IsOn = _settings.CoverSpin;
        AccentColorPicker.Color = ParseHex(string.IsNullOrEmpty(_settings.AccentColor) ? "#31c27c" : _settings.AccentColor);

        // Data/cache location.
        CacheDirBox.Text = DataLocation.Root;
        CacheDirStatus.Text = DataLocation.IsCustom
            ? "当前为自定义位置（默认：%LOCALAPPDATA%\\MusicPlayer）。"
            : "当前使用默认位置：%LOCALAPPDATA%\\MusicPlayer。";
        CacheDirStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)RootGrid.Resources["TextSecondary"];
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
        ApplyStyleLive();
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
        var sw = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        var sh = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

        int w = _settings.WindowW > 0 ? Math.Min(_settings.WindowW, sw) : 1380;
        int h = _settings.WindowH > 0 ? Math.Min(_settings.WindowH, sh) : 860;

        int x = _settings.WindowX >= 0 ? _settings.WindowX : (sw - w) / 2;
        int y = _settings.WindowY >= 0 ? _settings.WindowY : (sh - h) / 2;
        x = Math.Max(0, Math.Min(x, sw - w));
        y = Math.Max(0, Math.Min(y, sh - h));

        NativeMethods.MoveWindow(hwnd, x, y, w, h, true);
    }

    private void MainWindow_Closed(object? sender, WindowEventArgs e)
    {
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
        var m = total / 60;
        var s = total % 60;
        return $"{m:D2}:{s:D2}";
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
