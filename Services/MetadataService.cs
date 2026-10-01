using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Models;
using TagLib;

namespace MusicPlayer.Services;

/// <summary>
/// Reads audio tags (title / artist / album / duration / embedded cover art)
/// using TagLibSharp, which supports mp3, flac, m4a, wav, ogg and more.
/// The heavy tag read happens on a background thread; UI updates are marshaled
/// back to the dispatcher.
/// </summary>
public static class MetadataService
{
    /// <summary>
    /// When true, track lists show the audio file name's title instead of the
    /// embedded tag title. MainWindow keeps this in sync with the setting and
    /// re-runs LoadAsync for every track when it flips.
    /// </summary>
    public static bool PreferFilenameTitles { get; set; }

    /// <summary>
    /// Resolve metadata for <paramref name="track"/> asynchronously.
    /// Reads go through <see cref="MetadataCache"/>: a warm start serves
    /// unchanged files from the persistent cache (no disk tag read at all)
    /// and only new/modified files hit TagLib.
    /// </summary>
    public static async Task LoadAsync(Track track, DispatcherQueue dispatcher, CancellationToken ct = default)
    {
        string? title = null;
        string? artist = null;
        string? album = null;
        TimeSpan duration = TimeSpan.Zero;
        byte[]? coverBytes = null;

        // --- Background: read tags (may touch disk / decode) ---
        await Task.Run(() =>
        {
            long mtime = 0;
            try
            {
                mtime = System.IO.File.GetLastWriteTimeUtc(track.Path).Ticks;
            }
            catch
            {
                // unreadable file → treated as cache miss, TagLib will fail the same way
            }

            if (MetadataCache.TryGet(track.Path, mtime, out title, out artist, out album, out duration))
                return; // warm path: cover comes from the disk cache below

            try
            {
                using var file = TagLib.File.Create(track.Path);
                var tag = file.Tag;
                if (tag != null)
                {
                    title = string.IsNullOrWhiteSpace(tag.Title) ? null : tag.Title.Trim();
                    artist = string.IsNullOrWhiteSpace(tag.FirstPerformer)
                        ? (string.IsNullOrWhiteSpace(tag.FirstAlbumArtist) ? null : tag.FirstAlbumArtist.Trim())
                        : tag.FirstPerformer.Trim();
                    album = string.IsNullOrWhiteSpace(tag.Album) ? null : tag.Album.Trim();

                    if (tag.Pictures != null && tag.Pictures.Length > 0)
                    {
                        var data = tag.Pictures[0].Data?.Data;
                        if (data != null && data.Length > 0)
                            coverBytes = data;
                    }
                }

                if (file.Properties != null)
                    duration = file.Properties.Duration;
            }
            catch
            {
                // Leave defaults on any read failure (unsupported format, DRM, ...).
            }

            if (mtime != 0)
                MetadataCache.Put(track.Path, mtime, title, artist, album, duration);
        }, ct);

        if (ct.IsCancellationRequested)
            return;

        // --- UI thread: build the cover bitmap (disk-cached) and apply metadata ---
        dispatcher.TryEnqueue(() =>
        {
            // TryEnqueue only takes a void handler, so the async work has to be
            // kicked off from here. ApplyMetadataAsync contains its own
            // exceptions — an unobserved one from an async void would crash.
            _ = ApplyMetadataAsync(track, title, artist, album, duration, coverBytes);
        });
    }

    /// <summary>
    /// Decode the cover off the UI thread where possible, then push everything
    /// onto the track. Stays on the UI thread so the change notifications from
    /// <see cref="Track.SetMetadata"/> are raised there.
    /// </summary>
    private static async Task ApplyMetadataAsync(
        Track track, string? title, string? artist, string? album,
        TimeSpan duration, byte[]? coverBytes)
    {
        ImageSource? cover;
        try
        {
            cover = await CoverCache.GetOrLoadAsync(track.Path, coverBytes);
        }
        catch
        {
            // Cover art is cosmetic — never let it fail the metadata update.
            cover = null;
        }

        track.SetMetadata(
            PreferFilenameTitles ? track.FileNameTitle : (title ?? track.FileNameTitle),
            artist ?? track.Artist,
            album ?? string.Empty,
            duration,
            cover);
    }
}
