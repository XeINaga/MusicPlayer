using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace MusicPlayer.Services;

/// <summary>
/// Sound-effect DSP state: a 10-band graphic equalizer with style presets
/// plus six effect knobs, in the spirit of QQ Music's 银河音效 panel.
///
/// This class is the in-memory mirror of the persisted <see cref="AppSettings"/>
/// fields and renders the state into an FFmpeg audio filter string that
/// PlaybackService passes to FFmpegInteropX when a track loads. When the
/// built chain is non-empty, every track routes through the FFmpeg decoder
/// (the system Media Foundation path cannot run these filters).
///
/// Filter choices are constrained by FFmpegInteropX's no-flush-at-EOS
/// behavior: anything with a deep look-ahead buffer (loudnorm, dynaudnorm)
/// loses its buffered tail when the song ends. Every filter used here is a
/// stateless-per-sample IIR or a short delay line (aecho's longest delay is
/// 90 ms), so the ending stays intact — verified with the ffdiag harness.
/// </summary>
public static class SoundFx
{
    /// <summary>EQ band center frequencies (Hz).</summary>
    public static readonly int[] BandFreqs = { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    /// <summary>Short labels shown under the EQ sliders.</summary>
    public static readonly string[] BandLabels = { "31", "62", "125", "250", "500", "1k", "2k", "4k", "8k", "16k" };

    /// <summary>Style presets: (persisted name, label, band gains in dB).</summary>
    public static readonly (string Name, string Label, double[] Gains)[] Presets =
    {
        ("Off",       "关闭",   new double[10]),
        ("Pop",       "流行",   new[] { -1.0, -1, 0, 2, 4, 4, 2, 0, -1, -1 }),
        ("Dance",     "舞曲",   new[] { 5.0, 4, 2, 0, 0, -1, -2, -2, 0, 1 }),
        ("Blues",     "蓝调",   new[] { 4.0, 3, 1, 1, -1, -1, 0, 1, 3, 3 }),
        ("Classical", "古典",   new[] { 4.0, 3, 2, 0, -1, -1, 0, 2, 3, 4 }),
        ("Jazz",      "爵士",   new[] { 3.0, 2, 1, 2, -1, -1, 0, 1, 2, 3 }),
        ("Slow",      "慢歌",   new[] { 2.0, 3, 1, 0, -2, -2, 0, 2, 3, 3 }),
        ("Electronic","电子乐", new[] { 5.0, 4, 1, 0, -2, 1, 1, 3, 4, 5 }),
        ("Rock",      "摇滚",   new[] { 5.0, 4, 3, 1, -1, -1, 1, 3, 4, 5 }),
        ("Country",   "乡村",   new[] { 3.0, 2, 1, 0, -1, -1, 0, 1, 2, 3 }),
        ("Vocal",     "人声",   new[] { -3.0, -2, 0, 3, 5, 5, 4, 2, 0, -2 }),
    };

    /// <summary>Master switch — when false no chain is built and tracks use
    /// the system decoder.</summary>
    public static bool Enabled { get; set; }

    /// <summary>Band gains in dB, clamped to -12..+12.</summary>
    public static double[] EqGains { get; private set; } = new double[10];

    /// <summary>Persisted preset name ("Custom" once a slider moves).</summary>
    public static string PresetName { get; set; } = "Off";

    /// <summary>High-frequency detail (treble), 0..100.</summary>
    public static double HiFi { get; set; }
    /// <summary>Small-room reverb (aecho decay), 0..100.</summary>
    public static double Reverb { get; set; }
    /// <summary>Stereo widening (stereowiden), 0..100.</summary>
    public static double Surround { get; set; }
    /// <summary>Bass shelf gain, 0..100 (→ 0..10 dB below 110 Hz).</summary>
    public static double BassBoost { get; set; }
    /// <summary>Dynamic punch (acompressor ratio + makeup), 0..100.</summary>
    public static double Punch { get; set; }
    /// <summary>Output channel balance, -100 (left) .. +100 (right).</summary>
    public static double Balance { get; set; }

    /// <summary>Copy the persisted values into the live state, sanitized.</summary>
    public static void LoadFrom(AppSettings s)
    {
        Enabled = s.SoundEffectsEnabled;
        PresetName = string.IsNullOrWhiteSpace(s.EqPreset) ? "Off" : s.EqPreset;
        var g = s.EqGains;
        for (var i = 0; i < 10; i++)
            EqGains[i] = g != null && i < g.Count ? Math.Clamp(g[i], -12, 12) : 0;
        HiFi = Math.Clamp(s.EffectHiFi, 0, 100);
        Reverb = Math.Clamp(s.EffectReverb, 0, 100);
        Surround = Math.Clamp(s.EffectSurround, 0, 100);
        BassBoost = Math.Clamp(s.EffectBass, 0, 100);
        Punch = Math.Clamp(s.EffectPunch, 0, 100);
        Balance = Math.Clamp(s.EffectBalance, -100, 100);
    }

    /// <summary>Write the live state back into the settings object.</summary>
    public static void SaveTo(AppSettings s)
    {
        s.SoundEffectsEnabled = Enabled;
        s.EqGains = EqGains.ToList();
        s.EqPreset = PresetName;
        s.EffectHiFi = HiFi;
        s.EffectReverb = Reverb;
        s.EffectSurround = Surround;
        s.EffectBass = BassBoost;
        s.EffectPunch = Punch;
        s.EffectBalance = Balance;
    }

    /// <summary>
    /// Build the FFmpeg audio filter chain; null when effects are off or every
    /// knob is neutral (in which case tracks load through the system decoder,
    /// keeping the native path for formats like mp3/flac).
    /// Numeric options are formatted invariantly — a comma decimal separator
    /// would split the filter string at the wrong place.
    /// </summary>
    public static string? BuildFilterChain()
    {
        if (!Enabled)
            return null;

        var parts = new List<string>(14);

        // 10-band peaking EQ, one octave wide; only non-zero bands are added.
        for (var i = 0; i < 10; i++)
        {
            var g = Math.Clamp(EqGains[i], -12, 12);
            if (Math.Abs(g) >= 0.25)
                parts.Add(Inv($"equalizer=f={BandFreqs[i]}:width_type=o:w=1.0:g={g:0.##}"));
        }

        if (BassBoost >= 0.5)
            parts.Add(Inv($"bass=g={BassBoost * 0.10:0.##}:f=110:width_type=q:w=0.7"));

        if (HiFi >= 0.5)
            parts.Add(Inv($"treble=g={HiFi * 0.04:0.##}:f=8000:width_type=q:w=0.7"));

        if (Reverb >= 0.5)
        {
            var t = Reverb / 100.0;
            // in_gain:out_gain:delays:decays — short early reflections only;
            // longer delay lines would smear the song's ending at EOS.
            parts.Add(Inv($"aecho=0.8:0.6:45|90:{0.16 * t:0.###}|{0.09 * t:0.###}"));
        }

        if (Surround >= 0.5)
        {
            var t = Surround / 100.0;
            parts.Add(
                Inv($"stereowiden=delay=20:feedback={0.45 * t:0.###}") +
                Inv($":crossfeed={0.45 * t:0.###}:drymix={1 - 0.3 * t:0.###}"));
        }

        if (Punch >= 0.5)
        {
            var t = Punch / 100.0;
            parts.Add(
                Inv($"acompressor=threshold=0.4:ratio={1 + 2.5 * t:0.##}") +
                Inv($":attack=25:release=300:makeup={1 + 0.9 * t:0.##}"));
        }

        if (Math.Abs(Balance) >= 0.5)
            parts.Add(Inv($"stereotools=balance_out={Balance / 100.0:0.###}"));

        return parts.Count == 0 ? null : string.Join(",", parts);
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
