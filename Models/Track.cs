using System;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace MusicPlayer.Models;

/// <summary>
/// Represents a single playable audio track in the playlist.
/// Implements <see cref="INotifyPropertyChanged"/> so the UI can react to
/// metadata (title / artist / album / cover / duration) resolved after load.
/// </summary>
public sealed class Track : INotifyPropertyChanged
{
    private string _title;
    private string _artist;
    private string _album = string.Empty;
    private TimeSpan _duration;
    private ImageSource? _cover;
    private DateTime _lastPlayed;
    private readonly DateTime _dateAdded = DateTime.Now;

    public Track(string path)
    {
        Path = path;
        var fileName = System.IO.Path.GetFileNameWithoutExtension(path);

        // Common naming convention: "Artist - Title"
        var sep = fileName.IndexOf(" - ", StringComparison.Ordinal);
        if (sep > 0 && sep < fileName.Length - 3)
        {
            _artist = fileName.Substring(0, sep).Trim();
            _title = fileName.Substring(sep + 3).Trim();
        }
        else
        {
            _title = fileName;
            _artist = "未知歌手";
        }
    }

    public string Path { get; }

    public string Title
    {
        get => _title;
        private set { _title = value; OnChanged(nameof(Title)); }
    }

    public string Artist
    {
        get => _artist;
        private set { _artist = value; OnChanged(nameof(Artist)); }
    }

    public string Album
    {
        get => _album;
        private set { _album = value; OnChanged(nameof(Album)); }
    }

    public TimeSpan Duration
    {
        get => _duration;
        private set { _duration = value; OnChanged(nameof(Duration)); }
    }

    public ImageSource? Cover
    {
        get => _cover;
        private set { _cover = value; OnChanged(nameof(Cover)); }
    }

    public DateTime LastPlayed
    {
        get => _lastPlayed;
        set { _lastPlayed = value; OnChanged(nameof(LastPlayed)); }
    }

    /// <summary>When this track was added to the library (used for "添加时间" sorting).</summary>
    public DateTime DateAdded => _dateAdded;

    /// <summary>
    /// Manually assigned lyric file (absolute path). When set, the lyrics engine
    /// uses this file directly instead of auto-detecting one next to the audio.
    /// Persisted in <c>lyricbindings.json</c> via <see cref="Services.LyricBindingStore"/>.
    /// </summary>
    public string? LyricPath { get; set; }

    /// <summary>Apply tag metadata (possibly resolved asynchronously from the file).</summary>
    public void SetMetadata(string title, string artist, string album, TimeSpan duration, ImageSource? cover)
    {
        Title = title;
        Artist = artist;
        Album = album;
        Duration = duration;
        Cover = cover;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
