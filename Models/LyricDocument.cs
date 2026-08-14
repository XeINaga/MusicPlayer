namespace MusicPlayer.Models;

/// <summary>
/// A single timed lyric line. Supports multiple languages:
/// the original text (e.g. Japanese), its romaji reading, and a translation.
/// </summary>
public sealed class LyricLine
{
    public System.TimeSpan Time { get; init; }

    public string? Original { get; set; }

    public string? Romaji { get; set; }

    public string? Translation { get; set; }
}

/// <summary>
/// A parsed lyric document (sorted by time) for one audio file.
/// </summary>
public sealed class LyricDocument
{
    public System.Collections.Generic.List<LyricLine> Lines { get; init; } = new();

    public bool HasRomaji => Lines.Exists(l => !string.IsNullOrWhiteSpace(l.Romaji));

    public bool HasTranslation => Lines.Exists(l => !string.IsNullOrWhiteSpace(l.Translation));
}
