using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MusicPlayer.Services;

/// <summary>One installed Wallpaper Engine wallpaper (from the Steam
/// workshop or the user's own projects folder).</summary>
public sealed class WeWallpaperInfo
{
    public string Title = "";
    public string Type = "";        // video | web | scene | application
    public string ProjectDir = "";
    public string FilePath = "";    // main content file (mp4 / html / scene.json)
    public string PreviewPath = ""; // preview image (may not exist)

    public string TypeDisplay => Type switch
    {
        "video" => "视频",
        "web" => "网页",
        "scene" => "场景",
        "application" => "应用",
        _ => Type,
    };
}

/// <summary>
/// Scans the locally installed Wallpaper Engine wallpapers: the Steam
/// workshop (app 431960) across all library folders, plus the user's own
/// projects. Each wallpaper folder is identified by its project.json
/// (title / type / file / preview).
/// </summary>
public static class WallpaperEngineLibrary
{
    private const uint WorkshopAppId = 431960;

    public static List<WeWallpaperInfo> Scan()
    {
        var list = new List<WeWallpaperInfo>();
        try
        {
            foreach (var root in SteamLibraryRoots())
            {
                var workshop = Path.Combine(root, "steamapps", "workshop", "content", WorkshopAppId.ToString());
                if (Directory.Exists(workshop))
                    foreach (var dir in Directory.EnumerateDirectories(workshop))
                        ScanProject(dir, list);

                var myProjects = Path.Combine(root, "steamapps", "common", "wallpaper_engine", "projects", "myprojects");
                if (Directory.Exists(myProjects))
                    foreach (var dir in Directory.EnumerateDirectories(myProjects))
                        ScanProject(dir, list);
            }
        }
        catch
        {
            // discovery is best effort
        }
        return list;
    }

    /// <summary>Steam library roots: the registry SteamPath plus every
    /// additional library listed in steamapps/libraryfolders.vdf.</summary>
    private static IEnumerable<string> SteamLibraryRoots()
    {
        var roots = new List<string>();
        try
        {
            var steam = (string?)Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null);
            if (!string.IsNullOrEmpty(steam))
                roots.Add(steam.Replace('/', '\\'));
        }
        catch { /* registry may be missing if Steam is not installed */ }

        try
        {
            var primary = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.ProgramFilesX86), "Steam");
            if (Directory.Exists(primary))
                roots.Add(primary);
        }
        catch { /* default location may not exist */ }

        // libraryfolders.vdf lists extra drives: "path"  "D:\\SteamLibrary"
        foreach (var root in roots.ToArray())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf))
                continue;
            try
            {
                foreach (var line in File.ReadLines(vdf))
                {
                    var trimmed = line.Trim();
                    if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var start = trimmed.IndexOf('"', 6);
                    var end = trimmed.LastIndexOf('"');
                    if (start < 0 || end <= start)
                        continue;
                    var lib = trimmed.Substring(start + 1, end - start - 1).Replace("\\\\", "\\");
                    if (Directory.Exists(lib) && !roots.Contains(lib, StringComparer.OrdinalIgnoreCase))
                        roots.Add(lib);
                }
            }
            catch { /* malformed vdf: skip */ }
        }
        return roots;
    }

    private static void ScanProject(string dir, List<WeWallpaperInfo> list)
    {
        try
        {
            var jsonPath = Path.Combine(dir, "project.json");
            if (!File.Exists(jsonPath))
                return;
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;

            string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
            string type = root.TryGetProperty("type", out var ty) ? ty.GetString() ?? "" : "";
            string file = root.TryGetProperty("file", out var f) ? f.GetString() ?? "" : "";
            string preview = root.TryGetProperty("preview", out var pv) ? pv.GetString() ?? "preview.jpg" : "preview.jpg";
            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(file))
                return;

            var filePath = Path.Combine(dir, file);
            if (!File.Exists(filePath))
                return;

            var previewPath = Path.Combine(dir, preview);
            list.Add(new WeWallpaperInfo
            {
                Title = string.IsNullOrEmpty(title) ? Path.GetFileName(dir) : title,
                Type = type,
                ProjectDir = dir,
                FilePath = filePath,
                PreviewPath = File.Exists(previewPath) ? previewPath : "",
            });
        }
        catch
        {
            // malformed project.json: skip the folder
        }
    }
}
