using System.IO;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>
/// Write files atomically: the content goes to a temp file first, which then
/// replaces the target. A crash mid-write can never leave a truncated/corrupt
/// JSON behind (the previous intact file survives until the swap).
/// Falls back to a plain overwrite when Replace is denied (read-only target,
/// ACLs, some network shares) so a save never crashes the caller.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, Encoding encoding)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents, encoding);

        try
        {
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }
        catch (Exception) when (File.Exists(path))
        {
            // Replace can fail on read-only / protected destinations.
            try { File.SetAttributes(path, FileAttributes.Normal); }
            catch { /* keep trying */ }

            try
            {
                File.Copy(tmp, path, true);
                TryDelete(tmp);
                return;
            }
            catch
            {
                // Last resort: a non-atomic direct write beats losing the data.
                File.WriteAllText(path, contents, encoding);
                TryDelete(tmp);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* best effort */ }
    }
}
