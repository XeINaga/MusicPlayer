using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using FFmpegInteropX;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
    // Holds Track references rather than indices so it stays correct when the
    // queue is edited — a deleted entry is simply skipped, and a moved one is
    // found at its new position.
    private readonly Stack<Track> _randomHistory = new();
    // Random mode: tracks stepped *back* from, so Next can retrace them.
    // Without it, Previous never records where it left and the back history
    // collapses to a single entry after one Previous/Next round trip.
    private readonly Stack<Track> _randomForward = new();
    // Random mode: indices not yet played in the current round. Drawing from
    // this until it empties plays every track once before any repeat.
    // It holds *indices*, so it is only meaningful for the exact queue it was
    // built from — _bagQueueCount detects add/remove, and structural edits
    // (reorder, queue swap) clear it explicitly.
    private readonly List<int> _randomBag = new();
    private int _bagQueueCount = -1;

    // Crossfade state: fades volume out over 200ms then loads the next track
    // and fades it in over CrossfadeDurationMs (0 = off, instant switch).
    private int _crossfadeDurationMs;
    private double _targetVolume = 1.0;
    private readonly DispatcherTimer _fadeTimer = new();
    private bool _fadingOut;
    private double _fadeFrom;
    private double _fadeTo;
    private TimeSpan _fadeElapsed;
    private static readonly TimeSpan FadeOutSpan = TimeSpan.FromMilliseconds(200);
    private const double FadeTickMs = 16; // ~60 fps

    public event Action<TimeSpan>? PositionTick;
    public event Action<MediaPlaybackState>? StateChanged;
    public event Action<int>? CurrentIndexChanged;
    public event Action? MediaOpened;
    /// <summary>Raised when the current item fails to decode/play (corrupt or
    /// unsupported file). Receives the MediaPlayer error message.</summary>
    public event Action<string>? MediaFailed;
    /// <summary>Raised when the play mode changes (SMTC shuffle/repeat buttons).</summary>
    public event Action? ModeChanged;

    /// <summary>Crossfade duration in ms. 0 = off (instant switch).</summary>
    public int CrossfadeDurationMs
    {
        get => _crossfadeDurationMs;
        set => _crossfadeDurationMs = Math.Max(0, value);
    }

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

        // Crossfade timer: ticks at ~60 fps, drives the volume ramp.
        _fadeTimer.Interval = TimeSpan.FromMilliseconds(FadeTickMs);
        _fadeTimer.Tick += OnFadeTick;
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
            // Switching into random starts a clean round instead of resuming a
            // pool left over from an earlier queue, and drops any half-finished
            // step back from the previous random session.
            if (_mode == PlayMode.Random)
            {
                ResetRandomBag();
                _randomForward.Clear();
            }
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
        _randomForward.Clear();
        ResetRandomBag();
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

    /// <summary>Point playback at <paramref name="index"/>, announce it and load it.</summary>
    private void GoTo(int index)
    {
        _index = index;
        CurrentIndexChanged?.Invoke(_index);

        if (_crossfadeDurationMs > 0)
        {
            _pendingTargetVolume = _targetVolume;
            StartCrossfade(index);
        }
        else
        {
            LoadCurrent(play: true);
        }
    }

    public void Next()
    {
        if (_queue == null || _queue.Count == 0)
            return;

        // Random mode: retrace a step back instead of spending a fresh draw,
        // so Previous followed by Next lands where the user started.
        if (_mode == PlayMode.Random && _randomForward.Count > 0)
        {
            var redo = PopValidRandomTrack(_randomForward);
            if (redo >= 0)
            {
                RememberRandomHistory();
                GoTo(redo);
                return;
            }
        }

        var n = ComputeNext(true);
        if (n < 0)
            return; // end of sequential list

        // A track the bag picked is new ground, not a redo: the forward path
        // ends here, same as navigating to a new page in a browser.
        _randomForward.Clear();
        RememberRandomHistory();
        GoTo(n);
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
            // Tracks deleted since they played are skipped over.
            var prev = PopValidRandomTrack(_randomHistory);
            if (prev < 0)
                return; // nothing remembered still exists in the queue

            // Record where we are leaving from, so Next can retrace it.
            RememberInto(_randomForward);
            GoTo(prev);
            return;
        }

        // Nothing recorded to step back through. In random mode this draws a
        // fresh track, so it needs the same bookkeeping as Next: drop a pending
        // redo and record the track being left. Without it the place we came
        // from is unrecoverable and a stale redo can resurface later.
        _randomForward.Clear();
        RememberRandomHistory();
        GoTo(ComputeNext(false));
    }

    public void MoveTo(int index)
    {
        if (_queue == null || index < 0 || index >= _queue.Count)
            return;

        if (_mode == PlayMode.Random)
        {
            // Hand-picked track: count it as played this round so the bag will
            // not hand it back until the next one.
            _randomBag.Remove(index);
            // Picking by hand is new ground, not a redo.
            _randomForward.Clear();
        }

        GoTo(index);
    }

    /// <summary>Adjust the internal index after a track is removed from the queue.</summary>
    public void ShiftIndex(int delta)
    {
        _index = Math.Max(-1, _index + delta);
        // Every index the round was built from has moved, so it has to go. The
        // count guard in ComputeNextRandom would usually catch this too, but
        // that only holds while removals are the sole edit — drop the round
        // explicitly rather than depend on it. History is kept on purpose: it
        // tracks Track objects, which a removal does not renumber.
        ResetRandomBag();
    }

    /// <summary>
    /// Re-point the current index without loading/playing anything — used after
    /// the user reorders the queue list, where the list itself is the queue.
    /// </summary>
    public void SetIndexSilent(int index)
    {
        if (_queue == null || index < 0 || index >= _queue.Count)
            return;
        _index = index;
        // Drag-reordering moves tracks between indices without changing the
        // count, so the length guard cannot catch it — every stored index now
        // points at a different track.
        ResetRandomBag();
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
        _randomForward.Clear();
        ResetRandomBag();
        _queue = tracks;
        _index = Math.Clamp(currentIndex, 0, tracks.Count - 1);
    }

    public void Clear()
    {
        _loadToken++; // invalidate any in-flight async load
        _fadeTimer.Stop();
        _fadingOut = false;
        _pendingCrossfadeIndex = -1;
        _player.Pause();
        _player.Source = null;
        _queue = null;
        _index = -1;
        // Unhook before dropping the reference. The session holds the handler,
        // so nulling the field alone leaks the session and keeps delivering
        // position ticks for a player that is no longer in use.
        if (_hookedSession != null)
        {
            _hookedSession.PositionChanged -= OnPositionChanged;
            _hookedSession = null;
        }
        _randomHistory.Clear();
        _randomForward.Clear();
        ResetRandomBag();

        _ffmpegSource?.Dispose();
        _ffmpegSource = null;
        _nativeSource?.Dispose();
        _nativeSource = null;

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
    // The plain (non-FFmpeg) path produces a MediaSource that owns a handle to
    // the audio file. It has to be released on the next swap, otherwise the
    // file stays locked (can't be renamed or deleted) and the underlying COM
    // objects pile up for the lifetime of the process.
    private MediaSource? _nativeSource;

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
            var oldNative = _nativeSource;
            _ffmpegSource = ffmpegSource;
            _nativeSource = nativeSource;
            _player.Source = source;
            // Only after the player has taken the new source: disposing the old
            // one while it is still current would cut playback.
            oldFfmpeg?.Dispose();
            oldNative?.Dispose();

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

        // If a crossfade was in progress (fade-out completed, new source just
        // opened), start the fade-in now.
        if (_pendingCrossfadeIndex >= 0)
        {
            var pending = _pendingCrossfadeIndex;
            // Clear the flag before anything can bail out. The fade-out can
            // finish into a queue that no longer holds this index (cleared, or
            // the track was filtered out), in which case LoadCurrent returns
            // early and MediaOpened never fires — leaving the flag set. A stale
            // one then zeroes the volume on some later, unrelated media open,
            // even after crossfade has been switched off entirely.
            _pendingCrossfadeIndex = -1;

            if (_crossfadeDurationMs > 0
                && _queue != null
                && pending >= 0
                && pending < _queue.Count
                && pending == _index)
            {
                _fadingOut = false;
                _fadeElapsed = TimeSpan.Zero;
                _fadeFrom = 0;
                _fadeTo = _pendingTargetVolume;
                _player.Volume = 0;
                _player.Play();
                if (!_fadeTimer.IsEnabled)
                    _fadeTimer.Start();
            }
        }
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

        // A track the bag picked is new ground, not a redo.
        _randomForward.Clear();
        RememberRandomHistory();
        GoTo(n);
    }

    private void RememberRandomHistory() => RememberInto(_randomHistory);

    /// <summary>Record the track being left, so a later step back can find it.</summary>
    private void RememberInto(Stack<Track> stack)
    {
        if (_mode != PlayMode.Random || _queue == null)
            return;
        if (_index < 0 || _index >= _queue.Count)
            return;
        stack.Push(_queue[_index]);
    }

    /// <summary>
    /// Pop the most recent entry of <paramref name="stack"/> that is still in
    /// the queue, discarding any whose track was removed since it was recorded.
    /// Returns -1 when nothing usable is left.
    /// </summary>
    private int PopValidRandomTrack(Stack<Track> stack)
    {
        if (_queue == null)
            return -1;

        while (stack.Count > 0)
        {
            var t = stack.Pop();
            // Track does not override Equals, so this is a reference lookup:
            // it resolves the entry wherever it sits now, or -1 if it is gone.
            var i = _queue.IndexOf(t);
            if (i >= 0)
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Random mode: draw the next track from the current round's pool, so every
    /// queued track plays once before any of them comes up again.
    /// </summary>
    private int ComputeNextRandom(int n)
    {
        if (n == 1)
            return 0;

        // Indices only line up with the queue the bag was built from; a length
        // change means tracks were added or removed, so start a fresh round.
        if (_bagQueueCount != n)
            RefillRandomBag(n);

        if (_randomBag.Count == 0)
            RefillRandomBag(n);

        // Draw from the tail: O(1) and leaves the remaining order untouched.
        var pick = _randomBag.Count - 1;
        var r = _randomBag[pick];

        // A fresh round puts every track back, including the one playing now.
        // Drawing it immediately would replay the same track twice in a row —
        // it reads as a broken skip, so take a different one and leave it in
        // the bag for later in the round.
        if (r == _index && _randomBag.Count > 1)
        {
            pick = _rnd.Next(_randomBag.Count - 1);
            r = _randomBag[pick];
        }

        _randomBag.RemoveAt(pick);
        return r;
    }

    /// <summary>Refill the round with every index, shuffled (Fisher-Yates).</summary>
    private void RefillRandomBag(int n)
    {
        _randomBag.Clear();
        for (var i = 0; i < n; i++)
            _randomBag.Add(i);

        for (var i = _randomBag.Count - 1; i > 0; i--)
        {
            var j = _rnd.Next(i + 1);
            (_randomBag[i], _randomBag[j]) = (_randomBag[j], _randomBag[i]);
        }
        _bagQueueCount = n;
    }

    /// <summary>Drop the current round; the next draw builds a fresh one.</summary>
    private void ResetRandomBag()
    {
        _randomBag.Clear();
        _bagQueueCount = -1;
    }

    private int ComputeNext(bool forward)
    {
        if (_queue == null || _queue.Count == 0)
            return -1;

        var n = _queue.Count;

        if (_mode == PlayMode.Random)
            return ComputeNextRandom(n);

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

    // ---------- Crossfade ----------

    /// <summary>
    /// Begin a crossfade transition: fade the current source out over 200 ms,
    /// then load the next track at the new index and fade it in over
    /// <see cref="CrossfadeDurationMs"/>. If crossfade is off (0) the caller
    /// should switch immediately instead.
    /// </summary>
    private void StartCrossfade(int nextIndex)
    {
        if (_fadeTimer.IsEnabled)
            _fadeTimer.Stop(); // cancel any in-progress fade

        _fadingOut = true;
        _fadeFrom = _player.Volume;
        _fadeTo = 0;
        _fadeElapsed = TimeSpan.Zero;
        _pendingCrossfadeIndex = nextIndex;
        _fadeTimer.Start();
    }

    private int _pendingCrossfadeIndex = -1;

    /// <summary>Target volume the next track should fade in to (user slider).</summary>
    private double _pendingTargetVolume = 1.0;

    private void OnFadeTick(object? sender, object? e)
    {
        var step = TimeSpan.FromMilliseconds(FadeTickMs);
        _fadeElapsed += step;

        if (_fadingOut)
        {
            var span = FadeOutSpan;
            var t = span.TotalMilliseconds > 0 ? Math.Clamp(_fadeElapsed.TotalMilliseconds / span.TotalMilliseconds, 0, 1) : 1;
            _player.Volume = Lerp(_fadeFrom, _fadeTo, t);

            if (t >= 1)
            {
                // Fade-out complete → load the new track.
                _fadingOut = false;
                _fadeElapsed = TimeSpan.Zero;
                _fadeFrom = 0;
                _fadeTo = _pendingTargetVolume;
                LoadCurrent(play: true);
                // Fade-in will be driven by the same timer (restarted below).
                // However, LoadCurrent is async; wait for MediaOpened to start
                // the fade-in so the player has actually decoded the first frame.
                return;
            }
        }
        else
        {
            // Fading in: ramp from 0 to target volume.
            var durMs = Math.Max(1, _crossfadeDurationMs);
            var t = Math.Clamp(_fadeElapsed.TotalMilliseconds / durMs, 0, 1);
            _player.Volume = Lerp(_fadeFrom, _fadeTo, t);

            if (t >= 1)
            {
                _fadeTimer.Stop();
                _player.Volume = _pendingTargetVolume;
            }
        }
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public TimeSpan Position => _player.PlaybackSession?.Position ?? TimeSpan.Zero;

    public TimeSpan Duration => _player.PlaybackSession?.NaturalDuration ?? TimeSpan.Zero;

    public double Volume
    {
        get => _player.Volume;
        set
        {
            var v = Math.Clamp(value, 0.0, 1.0);
            _player.Volume = v;
            _targetVolume = v;
            // A fade-in ramps toward _fadeTo and settles on
            // _pendingTargetVolume, so a slider move during a crossfade has to
            // reach them. Leaving them alone means the next tick overwrites the
            // level the user just picked with the old one, and it only comes
            // back after a restart (the persisted target is the new value).
            if (!_fadingOut && _fadeTimer.IsEnabled)
                _fadeTo = v;
            _pendingTargetVolume = v;
        }
    }

    /// <summary>
    /// The level the user picked. Unlike <see cref="Volume"/> this is untouched
    /// by an in-flight crossfade, so it is the value to persist — saving the
    /// live one during a fade would store something near zero and start muted.
    /// </summary>
    public double TargetVolume => _targetVolume;

    public double Rate
    {
        get => _player.PlaybackRate;
        set => _player.PlaybackRate = Math.Clamp(value, 0.5, 2.0);
    }

    public MediaPlaybackState PlaybackState =>
        _player.PlaybackSession?.PlaybackState ?? (MediaPlaybackState)0;
}
