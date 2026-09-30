using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
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
    // Resolved per access (not cached at type init) so a user-changed
    // data directory takes effect like everywhere else.
    private static string CacheDir => Path.Combine(DataLocation.Root, "covers");

    /// <summary>
    /// Return a decoded cover bitmap.  If the disk cache already holds
    /// an entry for <paramref name="audioPath"/> it is loaded directly;
    /// otherwise <paramref name="rawBytes"/> (from TagLib) is persisted and
    /// then loaded back from disk.
    /// </summary>
    /// <param name="audioPath">Full path of the audio file (used to derive cache key).</param>
    /// <param name="rawBytes">Raw image bytes extracted from the audio tag (may be null).</param>
    /// <returns>
    /// A <see cref="BitmapImage"/> with <see cref="BitmapImage.DecodePixelWidth"/>
    /// set to 480, or <c>null</c> when no cover art is available.
    /// </returns>
    /// <remarks>
    /// Must be called on the UI thread — BitmapImage is a DependencyObject. The
    /// awaits inside release the thread rather than blocking it; the decode
    /// itself stays asynchronous.
    /// </remarks>
    public static async Task<BitmapImage?> GetOrLoadAsync(string audioPath, byte[]? rawBytes)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);

            // Key includes the audio file's mtime so re-tagged/replaced
            // artwork invalidates the stale cached image.
            long mtime = 0;
            try { mtime = File.GetLastWriteTimeUtc(audioPath).Ticks; } catch { }
            var cacheFile = Path.Combine(CacheDir, HashPath(audioPath) + "." + mtime.ToString("x") + ".png");

            if (File.Exists(cacheFile))
                return CreateFromDisk(cacheFile);

            if (rawBytes == null || rawBytes.Length == 0)
                return null;

            // Slow path: writing the extracted art is a synchronous write of up
            // to a couple of megabytes. On the UI thread that stalls scrolling,
            // so push it to the thread pool. Reading it back via UriSource is
            // already asynchronous, and is how the fast path works too.
            await Task.Run(() =>
            {
                var tmp = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(tmp, rawBytes);
                try { File.Move(tmp, cacheFile, true); }
                catch { try { File.Delete(tmp); } catch { } }
            });

            return CreateFromDisk(cacheFile);
        }
        catch
        {
            // On any I/O or codec failure fall back to an in-memory bitmap so
            // the UI still shows *something*.
            return rawBytes == null ? null : await CreateInMemoryAsync(rawBytes);
        }
    }

    public static Task TrimAsync(long maxBytes = 256L * 1024 * 1024)
    {
        return Task.Run(() =>
        {
            try
            {
                if (!Directory.Exists(CacheDir))
                    return;

                var files = new DirectoryInfo(CacheDir)
                    .EnumerateFiles("*.png")
                    .OrderByDescending(f => f.LastAccessTimeUtc)
                    .ToList();
                long total = files.Sum(f => f.Length);
                foreach (var file in files.OrderBy(f => f.LastAccessTimeUtc))
                {
                    if (total <= maxBytes)
                        break;
                    total -= file.Length;
                    try { file.Delete(); } catch { }
                }
            }
            catch
            {
            }
        });
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

    private static async Task<BitmapImage?> CreateInMemoryAsync(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.DecodePixelWidth = 480;
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            stream.Seek(0);
            // SetSourceAsync decodes without blocking the calling thread;
            // SetSource decodes inline and would freeze a scrolling list.
            await bmp.SetSourceAsync(stream);
            return bmp;
        }
        catch
        {
            return null;
        }
    }
}
