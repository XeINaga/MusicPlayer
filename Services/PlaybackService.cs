using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using FFmpegInteropX;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;
using MusicPlayer.Models;

namespace MusicPlayer.Services;

/// <summary>
/// Playback modes for the bottom-bar control.
/// </summary>
public enum PlayMode
{
    Sequential,  // 顺序播放 (stop at end)
    LoopAll,     // 列表循环
    LoopOne,     // 单曲循环
    Random       // 随机播放
}

/// <summary>
/// Wraps a headless <see cref="MediaPlayer"/> and manages a playback queue
/// (IList&lt;Track&gt;) with explicit play modes (sequential / loop-all /
/// loop-one / random). Each track is loaded as its own <see cref="MediaSource"/>
/// so the app keeps full control over advancing — essential for random and
/// single-track repeat. All callbacks are marshaled to the UI dispatcher.
/// </summary>
public sealed class PlaybackService
{
    private readonly MediaPlayer _player = new();
    private readonly DispatcherQueue _dispatcher;
    private IList<Track>? _queue;
    private int _index = -1;
    private PlayMode _mode = PlayMode.Sequential;
    private TimeSpan? _pendingSeek;
    private MediaPlaybackSession? _hookedSession;
    private readonly Random _rnd = new();
    // Random mode: real "previous" needs a history of what actually played.
    private readonly Stack<int> _randomHistory = new();

    public event Action<TimeSpan>? PositionTick;
    public event Action<MediaPlaybackState>? StateChanged;
    public event Action<int>? CurrentIndexChanged;
    public event Action? MediaOpened;
    /// <summary>Raised when the current item fails to decode/play (corrupt or
    /// unsupported file). Receives the MediaPlayer error message.</summary>
    public event Action<string>? MediaFailed;
    /// <summary>Raised when the play mode changes (SMTC shuffle/repeat buttons).</summary>
    public event Action? ModeChanged;

    private SystemMediaTransportControls? _smtc;
    private bool _smtcBound;
    private DateTime _lastSmtcTimeline = DateTime.MinValue;

    public PlaybackService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _player.AutoPlay = false;
        _player.MediaOpened += (_, _) => _dispatcher.TryEnqueue(OnMediaOpened);
        _player.MediaEnded += (_, _) => _dispatcher.TryEnqueue(OnMediaEnded);
        _player.MediaFailed += (_, e) =>
            _dispatcher.TryEnqueue(() => MediaFailed?.Invoke(e.ErrorMessage ?? string.Empty));
        _player.CurrentStateChanged += (_, _) =>
            _dispatcher.TryEnqueue(() => { UpdateSmtcPlaybackStatus(); StateChanged?.Invoke(PlaybackState); });

        // SMTC: media keys / bluetooth headset / volume flyout / lock screen.
        // Play & pause are handled by the CommandManager itself; next/previous
        // (and shuffle/repeat) arrive as *Received events because we drive a
        // plain per-track MediaSource instead of a MediaPlaybackList.
        _player.CommandManager.IsEnabled = true;
        _player.CommandManager.NextReceived += (s, e) =>
        {
            e.Handled = true;
            _dispatcher.TryEnqueue(Next);
        };
        _player.CommandManager.PreviousReceived += (s, e) =>
        {
            e.Handled = true;
            _dispatcher.TryEnqueue(Previous);
        };

        _smtc = _player.SystemMediaTransportControls;
        if (_smtc != null)
        {
            _smtc.ButtonPressed += OnSmtcButtonPressed;
            _smtcBound = true;
        }
    }

    // Play/Pause/Next/Previous normally arrive through the CommandManager
    // handlers above; ButtonPressed is the fallback when it does not own them
    // (e.g. some hardware remotes send raw button events).
    private void OnSmtcButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        switch (args.Button)
        {
            case SystemMediaTransportControlsButton.Play:
                _dispatcher.TryEnqueue(Play);
                break;
            case SystemMediaTransportControlsButton.Pause:
                _dispatcher.TryEnqueue(Pause);
                break;
        }
    }

    public PlayMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;
            _mode = value;
            ModeChanged?.Invoke();
        }
    }

    public IList<Track>? Queue => _queue;

    public int CurrentIndex => _index;

    /// <summary>
    /// Replace the queue and (unless <paramref name="autoPlay"/> is false) start
    /// playing at <paramref name="startIndex"/>, optionally resuming from <paramref name="resume"/>.
    /// </summary>
    public void SetQueue(IList<Track> tracks, int startIndex, TimeSpan? resume = null, bool autoPlay = true)
    {
        if (tracks == null || tracks.Count == 0)
            return;

        _randomHistory.Clear();
        _queue = tracks;

        // A negative index means "just adopt the queue, load nothing" — clamping
        // it to 0 would silently load (and later "play") an arbitrary track.
        if (startIndex < 0)
        {
            _index = -1;
            return;
        }

        _index = Math.Clamp(startIndex, 0, tracks.Count - 1);
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent(resume, autoPlay);
    }

    public void Play() => _player.Play();

    public void Pause() => _player.Pause();

    public void PlayPause()
    {
        if (PlaybackState == MediaPlaybackState.Playing)
            _player.Pause();
        else
            _player.Play();
    }

    public void Next()
    {
        if (_queue == null || _queue.Count == 0)
            return;

        var n = ComputeNext(true);
        if (n < 0)
            return; // end of sequential list

        RememberRandomHistory();
        _index = n;
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent(play: true);
    }

    public void Previous()
    {
        if (_queue == null || _queue.Count == 0)
            return;

        // Restart current track if we are more than 3s in.
        if (Position.TotalSeconds > 3)
        {
            Seek(TimeSpan.Zero);
            return;
        }

        // Random mode: go back through what actually played before.
        if (_mode == PlayMode.Random && _randomHistory.Count > 0)
        {
            _index = _randomHistory.Pop();
            CurrentIndexChanged?.Invoke(_index);
            LoadCurrent(play: true);
            return;
        }

        _index = ComputeNext(false);
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent(play: true);
    }

    public void MoveTo(int index)
    {
        if (_queue == null || index < 0 || index >= _queue.Count)
            return;

        _index = index;
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent(play: true);
    }

    /// <summary>Adjust the internal index after a track is removed from the queue.</summary>
    public void ShiftIndex(int delta) => _index = Math.Max(-1, _index + delta);

    /// <summary>
    /// Re-point the current index without loading/playing anything — used after
    /// the user reorders the queue list, where the list itself is the queue.
    /// </summary>
    public void SetIndexSilent(int index)
    {
        if (_queue == null || index < 0 || index >= _queue.Count)
            return;
        _index = index;
    }

    /// <summary>
    /// Swap the whole queue for another list while keeping the current track
    /// (no reload, no playback interruption) — used by "play next", which
    /// snapshots the queue into a dedicated list instead of mutating the
    /// library / a user playlist.
    /// </summary>
    public void ReplaceQueueSilent(IList<Track> tracks, int currentIndex)
    {
        if (tracks == null || tracks.Count == 0)
            return;
        _randomHistory.Clear();
        _queue = tracks;
        _index = Math.Clamp(currentIndex, 0, tracks.Count - 1);
    }

    public void Clear()
    {
        _loadToken++; // invalidate any in-flight async load
        _player.Pause();
        _player.Source = null;
        _queue = null;
        _index = -1;
        _hookedSession = null;

        _ffmpegSource?.Dispose();
        _ffmpegSource = null;

        if (_smtcBound && _smtc != null)
        {
            try
            {
                _smtc.DisplayUpdater.ClearAll();
            }
            catch
            {
                // best-effort
            }
            UpdateSmtcPlaybackStatus();
        }
    }

    // Formats Windows Media Foundation can't decode natively — routed through
    // FFmpegInteropX so ogg/opus/ape/... actually play instead of failing.
    private static readonly HashSet<string> FfmpegExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ogg", ".opus", ".ape", ".tak", ".wv", ".mka"
    };

    private int _loadToken;
    private FFmpegMediaSource? _ffmpegSource;

    /// <summary>
    /// Load the track at the current index. Native-MF formats go through
    /// StorageFile (fixes paths with '#'/'?'), everything else through FFmpeg.
    /// Async: a superseded load (fast track switching) aborts silently.
    /// </summary>
    private async void LoadCurrent(TimeSpan? resume = null, bool play = false)
    {
        if (_queue == null || _index < 0 || _index >= _queue.Count)
            return;

        var path = _queue[_index]?.Path;
        if (string.IsNullOrEmpty(path))
            return;

        var token = ++_loadToken;
        _pendingSeek = resume;

        IMediaPlaybackSource? source = null;
        MediaSource? nativeSource = null;
        FFmpegMediaSource? ffmpegSource = null;

        try
        {
            if (FfmpegExtensions.Contains(Path.GetExtension(path)))
            {
                ffmpegSource = await FFmpegMediaSource.CreateFromUriAsync(path);
                if (token != _loadToken) { ffmpegSource.Dispose(); return; }
                source = ffmpegSource.CreateMediaPlaybackItem();
            }
            else
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                if (token != _loadToken) return;
                nativeSource = MediaSource.CreateFromStorageFile(file);
                source = nativeSource;
            }

            if (token != _loadToken)
            {
                ffmpegSource?.Dispose();
                nativeSource?.Dispose();
                return;
            }

            // Swap sources, then release the previous FFmpeg wrapper.
            var oldFfmpeg = _ffmpegSource;
            _ffmpegSource = ffmpegSource;
            _player.Source = source;
            oldFfmpeg?.Dispose();

            HookSession();
            UpdateSmtcDisplay();

            if (play)
                _player.Play();
        }
        catch (Exception ex)
        {
            ffmpegSource?.Dispose();
            nativeSource?.Dispose();
            if (token == _loadToken)
                _dispatcher.TryEnqueue(() => MediaFailed?.Invoke(ex.Message));
        }
    }

    // ---------- SMTC (system media controls) ----------

    private void UpdateSmtcDisplay()
    {
        if (!_smtcBound || _smtc == null)
            return;

        var track = (_queue != null && _index >= 0 && _index < _queue.Count) ? _queue[_index] : null;
        var updater = _smtc.DisplayUpdater;
        updater.Type = MediaPlaybackType.Music;
        updater.MusicProperties.Title = track?.Title ?? "未在播放";
        updater.MusicProperties.Artist = string.IsNullOrEmpty(track?.Artist) ? " " : track!.Artist;
        updater.MusicProperties.AlbumTitle = track?.Album ?? string.Empty;
        updater.Thumbnail = null;
        updater.Update();

        if (track != null)
            UpdateSmtcThumbnail(track.Path);

        UpdateSmtcPlaybackStatus();
    }

    /// <summary>Pull the embedded cover once more for the SMTC thumbnail
    /// (volume flyout / lock screen art) — cheap enough per track change.</summary>
    private async void UpdateSmtcThumbnail(string path)
    {
        try
        {
            var bytes = await Task.Run<byte[]?>(() =>
            {
                try
                {
                    using var file = TagLib.File.Create(path);
                    return file.Tag.Pictures?.FirstOrDefault()?.Data?.Data;
                }
                catch
                {
                    return null;
                }
            });

            if (bytes == null || bytes.Length == 0 || !_smtcBound || _smtc == null)
                return;

            var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            _smtc.DisplayUpdater.Thumbnail = RandomAccessStreamReference.CreateFromStream(stream);
            _smtc.DisplayUpdater.Update();
        }
        catch
        {
            // best-effort: no thumbnail is fine
        }
    }

    private void UpdateSmtcPlaybackStatus()
    {
        if (!_smtcBound || _smtc == null)
            return;

        _smtc.PlaybackStatus = PlaybackState switch
        {
            MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
            MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
            MediaPlaybackState.Buffering or MediaPlaybackState.Opening => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped
        };
    }

    private void UpdateSmtcTimeline(bool force = false)
    {
        if (!_smtcBound || _smtc == null)
            return;

        var now = DateTime.UtcNow;
        if (!force && (now - _lastSmtcTimeline).TotalMilliseconds < 900)
            return;
        _lastSmtcTimeline = now;

        var dur = Duration;
        if (dur <= TimeSpan.Zero)
            return;

        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            Position = Position,
            StartTime = TimeSpan.Zero,
            EndTime = dur,
            MinSeekTime = TimeSpan.Zero,
            MaxSeekTime = dur
        });
    }

    private void HookSession()
    {
        var s = _player.PlaybackSession;
        if (s == _hookedSession)
            return;

        if (_hookedSession != null)
            _hookedSession.PositionChanged -= OnPositionChanged;

        _hookedSession = s;
        if (s != null)
            s.PositionChanged += OnPositionChanged;
    }

    private void OnPositionChanged(object? sender, object? args)
    {
        var pos = Position;
        _dispatcher.TryEnqueue(() =>
        {
            PositionTick?.Invoke(pos);
            UpdateSmtcTimeline();
        });
    }

    private void OnMediaOpened()
    {
        HookSession();
        if (_pendingSeek.HasValue)
        {
            Seek(_pendingSeek.Value);
            _pendingSeek = null;
        }

        UpdateSmtcTimeline(force: true);
        MediaOpened?.Invoke();
    }

    private void OnMediaEnded()
    {
        if (_mode == PlayMode.LoopOne)
        {
            Seek(TimeSpan.Zero);
            _player.Play();
            return;
        }

        var n = ComputeNext(true);
        if (n < 0)
            return; // stop at the end of a sequential list

        RememberRandomHistory();
        _index = n;
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent(play: true);
    }

    private void RememberRandomHistory()
    {
        if (_mode == PlayMode.Random && _index >= 0)
            _randomHistory.Push(_index);
    }

    private int ComputeNext(bool forward)
    {
        if (_queue == null || _queue.Count == 0)
            return -1;

        var n = _queue.Count;

        if (_mode == PlayMode.Random)
        {
            if (n == 1)
                return 0;
            int r;
            do { r = _rnd.Next(n); } while (r == _index);
            return r;
        }

        if (forward)
        {
            var ni = _index + 1;
            if (ni >= n)
                return _mode == PlayMode.LoopAll ? 0 : -1;
            return ni;
        }

        var pi = _index - 1;
        if (pi < 0)
            return _mode == PlayMode.LoopAll ? n - 1 : 0;
        return pi;
    }

    public void Seek(TimeSpan position)
    {
        if (_player.PlaybackSession != null)
            _player.PlaybackSession.Position = position;
    }

    public TimeSpan Position => _player.PlaybackSession?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => _player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0.0, 1.0);
    }

    public MediaPlaybackState PlaybackState =>
        _player.PlaybackSession?.PlaybackState ?? (MediaPlaybackState)0;
}
