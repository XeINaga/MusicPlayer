using System.IO;
using System.IO.Compression;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>
/// Decrypts KuGou's KRC lyric format: "krc1" magic (4 plain bytes) followed
/// by a zlib stream whose every byte was XOR'd with a fixed 17-byte key.
/// The plaintext is a word-timed lyric ("[start,dur]word&lt;start,dur,0&gt;..."),
/// UTF-8; word times are RELATIVE to their line's start (unlike QRC/YRC).
/// </summary>
public static class KrcDecrypter
{
    // "@Gaw^2tGQ61-" + CE D2 6E 69 — verified against live downloads.
    private static readonly byte[] Key =
        { 0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69 };

    public static string? Decrypt(byte[] data)
    {
        if (data == null || data.Length < 8 || data[0] != (byte)'k')
            return null;

        var body = new byte[data.Length - 4];
        for (var i = 0; i < body.Length; i++)
            body[i] = (byte)(data[i + 4] ^ Key[i % Key.Length]);

        try
        {
            using var input = new MemoryStream(body);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            // Lyrics are plain text — even a huge word-timed book is well under
            // a megabyte. An adversarial .krc (a local file, or a payload
            // injected before this endpoint moved to HTTPS) could otherwise
            // decompress into gigabytes and take the process down.
            var chunk = new byte[64 * 1024];
            long total = 0;
            int read;
            while ((read = zlib.Read(chunk, 0, chunk.Length)) > 0)
            {
                total += read;
                if (total > 16 * 1024 * 1024)
                    return null;
                output.Write(chunk, 0, read);
            }
            var text = Encoding.UTF8.GetString(output.ToArray());
            return text.Length == 0 ? null : text;
        }
        catch
        {
            // Wrong key bytes / not zlib — caller falls back to another source.
            return null;
        }
    }
}
