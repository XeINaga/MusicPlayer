namespace MusicPlayer.Models;

/// <summary>
/// One timed syllable/word inside a word-level lyric line (QRC / YRC / KRC).
/// Start and Duration are absolute (relative to the song, like the line time).
/// </summary>
public sealed record LyricWord(TimeSpan Start, TimeSpan Duration, string Text);

/// <summary>
/// A single timed lyric line. Supports multiple languages:
/// the original text (e.g. Japanese), its romaji reading, and a translation.
/// Word-level timing (karaoke) lives in <see cref="Words"/> when the source
/// lyric carried it (QRC/YRC/KRC); null for plain LRC/SRT lines.
/// </summary>
public sealed class LyricLine
{
    public System.TimeSpan Time { get; init; }

    public string? Original { get; set; }

    public string? Romaji { get; set; }

    public string? Translation { get; set; }

    public System.Collections.Generic.List<LyricWord>? Words { get; set; }
}

/// <summary>
/// A parsed lyric document (sorted by time) for one audio file.
/// </summary>
public sealed class LyricDocument
{
    public System.Collections.Generic.List<LyricLine> Lines { get; init; } = new();

    public bool HasRomaji => Lines.Exists(l => !string.IsNullOrWhiteSpace(l.Romaji));

    public bool HasTranslation => Lines.Exists(l => !string.IsNullOrWhiteSpace(l.Translation));

    /// <summary>True when at least one line carries word-level timing.</summary>
    public bool IsWordTimed => Lines.Exists(l => l.Words is { Count: > 0 });
}
