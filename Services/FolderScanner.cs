using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MusicPlayer.Services;

/// <summary>
/// Enumerates audio files inside a folder, supporting three modes:
///  - single file
///  - recursive (with a depth limit + file-count limit as an interruption mechanism)
///  - flat (current folder only, no recursion)
///
/// The recursive walk fans sub-directories out to a small worker pool: a large
/// library spans many folders and the directory enumeration (thousands of
/// FindFirstFile round-trips) dominates the scan, so running 4-8 directory
/// walks concurrently cuts wall time roughly by the core count. Results are
/// returned sorted so the library order is deterministic regardless of thread
/// scheduling.
/// </summary>
public static class FolderScanner
{
    /// <summary>Audio extensions supported via Windows Media Foundation.</summary>
    public static readonly string[] AudioExtensions =
    {
        ".mp3", ".flac", ".m4a", ".aac", ".wav", ".wma",
        ".ogg", ".opus", ".ape", ".tak", ".m4b", ".mp4", ".mka", ".wv"
    };

    private static readonly HashSet<string> AudioExtensionSet =
        new(AudioExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>Maximum recursion depth when adding a folder recursively.</summary>
    public const int DefaultMaxDepth = 8;

    /// <summary>Hard cap on the number of files collected (abort scan beyond this).</summary>
    public const int DefaultMaxFiles = 5000;

    public static bool IsAudio(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 0 && AudioExtensionSet.Contains(ext);
    }

    /// <summary>
    /// Collect audio file paths.
    /// </summary>
    /// <param name="root">Folder path.</param>
    /// <param name="recursive">When true, descend into sub-folders up to <paramref name="maxDepth"/>.</param>
    /// <param name="maxDepth">Recursion depth guard (interruption mechanism).</param>
    /// <param name="maxFiles">Maximum number of files to collect (interruption mechanism).</param>
    public static List<string> Scan(string root, bool recursive, int maxDepth = DefaultMaxDepth, int maxFiles = DefaultMaxFiles)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return result;

        if (recursive)
            result = ScanRecursive(root, maxDepth, maxFiles);
        else
            ScanFlat(root, maxFiles, result);

        // Deterministic order (the parallel walk is nondeterministic otherwise).
        if (result.Count > 1)
            result.Sort(StringComparer.OrdinalIgnoreCase);
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

    private static List<string> ScanRecursive(string root, int maxDepth, int maxFiles)
    {
        var found = new List<string>();
        var lockObj = new object();
        var dirs = new Queue<(string Path, int Depth)>();
        dirs.Enqueue((root, 0));
        var workers = new Task[Math.Max(2, Math.Min(Environment.ProcessorCount, 8))];

        for (var w = 0; w < workers.Length; w++)
        {
            workers[w] = Task.Run(() =>
            {
                while (true)
                {
                    string dir;
                    int depth;
                    lock (dirs)
                    {
                        if (dirs.Count == 0 || found.Count >= maxFiles)
                            return;
                        (dir, depth) = dirs.Dequeue();
                    }

                    try
                    {
                        var audioHere = new List<string>();
                        var subDirs = new List<string>();
                        foreach (var f in Directory.EnumerateFiles(dir))
                        {
                            if (IsAudio(f))
                            {
                                audioHere.Add(f);
                                if (audioHere.Count + found.Count >= maxFiles)
                                    break;
                            }
                        }
                        if (depth < maxDepth)
                        {
                            foreach (var sub in Directory.EnumerateDirectories(dir))
                                subDirs.Add(sub);
                        }

                        lock (lockObj)
                        {
                            foreach (var f in audioHere)
                            {
                                if (found.Count >= maxFiles)
                                    break;
                                found.Add(f);
                            }
                            if (found.Count < maxFiles)
                            {
                                foreach (var sub in subDirs)
                                    dirs.Enqueue((sub, depth + 1));
                            }
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
            });
        }

        Task.WaitAll(workers);
        return found;
    }
}
