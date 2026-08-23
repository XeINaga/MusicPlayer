using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
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
    /// Resolve metadata for <paramref name="track"/> asynchronously.
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
        }, ct);

        if (ct.IsCancellationRequested)
            return;

        // --- UI thread: build the cover bitmap and apply metadata ---
        dispatcher.TryEnqueue(() =>
        {
            ImageSource? cover = null;
            if (coverBytes != null)
                cover = CreateImage(coverBytes);

            track.SetMetadata(
                title ?? track.Title,
                artist ?? track.Artist,
                album ?? string.Empty,
                duration,
                cover);
        });
    }

    private static BitmapImage? CreateImage(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            // Decode downscaled: covers are commonly 1000–3000px; keeping them
            // at full resolution for thousands of tracks wastes gigabytes.
            // 480px covers the 164px cards and the 232px vinyl at 1.5x DPI.
            bmp.DecodePixelWidth = 480;
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            writer.StoreAsync().GetResults();
            stream.Seek(0);
            bmp.SetSource(stream);
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
