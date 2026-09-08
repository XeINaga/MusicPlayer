using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace MusicPlayer.Models;

public enum LyricTaskStatus
{
    /// <summary>Queued, not started yet.</summary>
    Pending,
    Running,
    /// <summary>Found something: a fresh lyric, or a missing translation / romaji.</summary>
    Success,
    /// <summary>Nothing matched, or the request threw.</summary>
    Failed,
    /// <summary>Queued but never reached because the run was stopped.</summary>
    Cancelled,
}

/// <summary>
/// One row in the lyric-completion panel: a track plus the live state of its
/// fill attempt. The panel shows hundreds of these, so everything the row
/// template needs is precomputed here rather than through converters.
/// </summary>
public sealed class LyricTaskItem : INotifyPropertyChanged
{
    public LyricTaskItem(Track track)
    {
        Track = track;
    }

    public Track Track { get; }

    public string Title => Track.Title;
    public string Artist => Track.Artist;

    /// <summary>
    /// True when the track has no main lyric at all and needs a fresh download;
    /// false when it already has one and only translation / romaji are missing.
    /// Set once when the queue is built, so it needs no change notification.
    /// </summary>
    public bool NeedsMainLyric { get; init; }

    private LyricTaskStatus _status = LyricTaskStatus.Pending;
    public LyricTaskStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusGlyph));
        }
    }

    /// <summary>Short human-readable outcome, e.g. "新下载歌词" / "未找到匹配".</summary>
    private string _detail = string.Empty;
    public string Detail
    {
        get => _detail;
        set
        {
            if (_detail == value) return;
            _detail = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Row accent. Assigned from code because the status colours live in
    /// ThemeDictionaries, which the plain resource indexer can't see — the
    /// panel re-stamps every row when the theme flips.
    /// </summary>
    private Brush? _statusBrush;
    public Brush? StatusBrush
    {
        get => _statusBrush;
        set
        {
            if (Equals(_statusBrush, value)) return;
            _statusBrush = value;
            OnPropertyChanged();
        }
    }

    public string StatusGlyph => Status switch
    {
        LyricTaskStatus.Running => "\uE72C",   // refresh — work in progress
        LyricTaskStatus.Success => "\uE73E",   // check mark
        LyricTaskStatus.Failed => "\uE8BB",    // cross
        LyricTaskStatus.Cancelled => "\uE8BB",
        _ => "\uE823",                         // clock — queued
    };

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
