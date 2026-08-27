using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace MusicPlayer.Services;

/// <summary>
/// Disk-backed cache for decoded cover-art bitmaps.
/// Avoids re-decoding embedded images on every launch.
/// Cached files live under %LOCALAPPDATA%\MusicPlayer\covers\{sha1}.png
/// </summary>
internal static class CoverCache
{
    private static readonly string CacheDir =
        Path.Combine(DataLocation.DefaultRoot, "covers");

    /// <summary>
    /// Return a decoded cover bitmap.  If the disk cache already holds
    /// an entry for <paramref name="audioPath"/> it is loaded directly;
    /// otherwise <paramref name="rawBytes"/> (from TagLib) is decoded,
    /// written to disk, and then returned.
    /// </summary>
    /// <param name="audioPath">Full path of the audio file (used to derive cache key).</param>
    /// <param name="rawBytes">Raw image bytes extracted from the audio tag (may be null).</param>
    /// <returns>
    /// A <see cref="BitmapImage"/> with <see cref="BitmapImage.DecodePixelWidth"/>
    /// set to 480, or <c>null</c> when no cover art is available.
    /// </returns>
    public static BitmapImage? GetOrLoad(string audioPath, byte[]? rawBytes)
    {
        if (rawBytes == null || rawBytes.Length == 0)
            return null;

        try
        {
            Directory.CreateDirectory(CacheDir);

            var cacheFile = Path.Combine(CacheDir, HashPath(audioPath) + ".png");

            // Fast path: load from disk cache
            if (File.Exists(cacheFile))
            {
                return CreateFromDisk(cacheFile);
            }

            // Slow path: decode, persist, then return
            File.WriteAllBytes(cacheFile, rawBytes);
            return CreateFromDisk(cacheFile);
        }
        catch
        {
            // On any I/O or codec failure fall back to an in-memory bitmap
            // so the UI still shows *something*.
            return CreateInMemory(rawBytes);
        }
    }

    /// <summary>
    /// Generate a SHA-1 hex string from the full audio file path.
    /// Using the full path (not just filename) avoids collisions when two
    /// tracks in different folders share the same name.
    /// </summary>
    private static string HashPath(string path)
    {
        var bytes = Encoding.UTF8.GetBytes(path);
        var hash = SHA1.HashData(bytes);
        var sb = new StringBuilder(40);
        foreach (var b in hash)
            sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static BitmapImage CreateFromDisk(string file)
    {
        var bmp = new BitmapImage();
        bmp.DecodePixelWidth = 480;
        bmp.UriSource = new Uri(file, UriKind.Absolute);
        return bmp;
    }

    private static BitmapImage CreateInMemory(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
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
