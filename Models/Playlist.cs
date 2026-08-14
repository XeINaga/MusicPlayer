using System.Collections.ObjectModel;
using MusicPlayer.Models;

namespace MusicPlayer.Models;

/// <summary>
/// A user-created playlist: a name plus an ordered collection of tracks.
/// Tracks are shared by reference with the master local library.
/// </summary>
public sealed class Playlist
{
    public string Name { get; set; } = "新建歌单";

    public ObservableCollection<Track> Tracks { get; } = new();
}
