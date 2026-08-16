using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MusicPlayer.Models;

namespace MusicPlayer.Models;

/// <summary>
/// A user-created playlist: a name plus an ordered collection of tracks.
/// Tracks are shared by reference with the master local library.
/// </summary>
public sealed class Playlist : INotifyPropertyChanged
{
    private string _name = "新建歌单";

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value)
                return;
            _name = value;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<Track> Tracks { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
