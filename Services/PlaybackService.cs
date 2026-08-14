using System;
using System.Collections.Generic;
using Microsoft.UI.Dispatching;
using Windows.Media.Core;
using Windows.Media.Playback;
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

    public event Action<TimeSpan>? PositionTick;
    public event Action<MediaPlaybackState>? StateChanged;
    public event Action<int>? CurrentIndexChanged;
    public event Action? MediaOpened;

    public PlaybackService()
    {
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _player.AutoPlay = false;
        _player.MediaOpened += (_, _) => _dispatcher.TryEnqueue(OnMediaOpened);
        _player.MediaEnded += (_, _) => _dispatcher.TryEnqueue(OnMediaEnded);
        _player.CurrentStateChanged += (_, _) =>
            _dispatcher.TryEnqueue(() => StateChanged?.Invoke(PlaybackState));
    }

    public PlayMode Mode
    {
        get => _mode;
        set => _mode = value;
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

        _queue = tracks;
        _index = Math.Clamp(startIndex, 0, tracks.Count - 1);
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent(resume);
        if (autoPlay)
            _player.Play();
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

        _index = n;
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent();
        _player.Play();
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

        _index = ComputeNext(false);
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent();
        _player.Play();
    }

    public void MoveTo(int index)
    {
        if (_queue == null || index < 0 || index >= _queue.Count)
            return;

        _index = index;
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent();
        _player.Play();
    }

    /// <summary>Adjust the internal index after a track is removed from the queue.</summary>
    public void ShiftIndex(int delta) => _index = Math.Max(-1, _index + delta);

    public void Clear()
    {
        _player.Pause();
        _player.Source = null;
        _queue = null;
        _index = -1;
        _hookedSession = null;
    }

    private void LoadCurrent(TimeSpan? resume = null)
    {
        if (_queue == null || _index < 0 || _index >= _queue.Count)
            return;

        var path = _queue[_index]?.Path;
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            _player.Source = MediaSource.CreateFromUri(new Uri(path));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"无法播放 {path}: {ex.Message}");
            return;
        }

        _pendingSeek = resume;
        HookSession();
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
        _dispatcher.TryEnqueue(() => PositionTick?.Invoke(pos));
    }

    private void OnMediaOpened()
    {
        HookSession();
        if (_pendingSeek.HasValue)
        {
            Seek(_pendingSeek.Value);
            _pendingSeek = null;
        }

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

        _index = n;
        CurrentIndexChanged?.Invoke(_index);
        LoadCurrent();
        _player.Play();
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
