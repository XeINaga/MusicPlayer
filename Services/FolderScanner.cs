using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MusicPlayer.Services;

/// <summary>
/// Enumerates audio files inside a folder, supporting three modes:
///  - single file
///  - recursive (with a depth limit + file-count limit as an interruption mechanism)
///  - flat (current folder only, no recursion)
/// </summary>
public static class FolderScanner
{
    /// <summary>Audio extensions supported via Windows Media Foundation.</summary>
    public static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma",
        ".ogg", ".opus", ".ape", ".tak", ".m4b", ".mp4", ".mka", ".wv"
    };

    /// <summary>Maximum recursion depth when adding a folder recursively.</summary>
    public const int DefaultMaxDepth = 8;

    /// <summary>Hard cap on the number of files collected (abort scan beyond this).</summary>
    public const int DefaultMaxFiles = 5000;

    public static bool IsAudio(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && AudioExtensions.Contains(ext, System.StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Collect audio file paths.
    /// </summary>
    /// <param name="root">Folder path.</param>
    /// <param name="recursive">When true, descend into sub-folders up to <paramref name="maxDepth"/>.</param>
    /// <param name="maxDepth">Recursion depth guard (interruption mechanism).</param>
    /// <param name="maxFiles">Maximum files to collect (interruption mechanism).</param>
    public static List<string> Scan(string root, bool recursive, int maxDepth = DefaultMaxDepth, int maxFiles = DefaultMaxFiles)
    {
        var result = new List<string>(maxFiles);
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return result;

        if (recursive)
            ScanRecursive(root, 0, maxDepth, maxFiles, result);
        else
            ScanFlat(root, maxFiles, result);

        return result;
    }

    private static void ScanFlat(string dir, int maxFiles, List<string> result)
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (result.Count >= maxFiles)
                    break;
                if (IsAudio(f))
                    result.Add(f);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // skip folders we cannot read
        }
        catch (IOException)
        {
            // skip on IO error
        }
    }

    private static void ScanRecursive(string dir, int depth, int maxDepth, int maxFiles, List<string> result)
    {
        // Interruption: stop descending once the depth limit is exceeded.
        if (depth > maxDepth)
            return;

        if (result.Count >= maxFiles)
            return;

        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (result.Count >= maxFiles)
                    return;
                if (IsAudio(f))
                    result.Add(f);
            }

            // Do not go deeper than the allowed depth.
            if (depth >= maxDepth)
                return;

            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                if (result.Count >= maxFiles)
                    return;
                ScanRecursive(sub, depth + 1, maxDepth, maxFiles, result);
            }
        }
        catch (UnauthorizedAccessException)
        {
            // skip folders we cannot read
        }
        catch (IOException)
        {
            // skip on IO error
        }
    }
}
