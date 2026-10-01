using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using MusicPlayer.Services;
using Windows.Graphics;
using Windows.Foundation;
using System.Runtime.InteropServices;

namespace MusicPlayer;

/// <summary>
/// Sound-effect panel: a 10-band graphic equalizer with style presets plus
/// six effect knobs, modeled on QQ Music's 银河音效 window. Styled with the
/// main window's theme tokens; size is remembered in settings.
///
/// Changes write through to <see cref="SoundFx"/> and settings.json
/// (debounced) and then ask the owner window to re-load the current track so
/// the effect is heard at once — a filter-chain change can only be applied by
/// rebuilding the media source.
/// </summary>
public sealed partial class SoundEffectWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IntPtr _ownerHwnd;
    private readonly Action _applyToPlayback;
    private readonly DispatcherTimer _commitTimer;
    private int _frameW, _frameH; // non-client overhead of the OS frame
    private bool _suppress;

    private readonly Slider[] _eqSliders = new Slider[10];
    private readonly TextBlock[] _eqValues = new TextBlock[10];
    private readonly Dictionary<string, ToggleButton> _presetButtons = new();
    private ToggleButton? _customChip;

    private static readonly (string Label, double Min, double Max, string Tip)[] EffectDefs =
    {
        ("高保真",   0, 100, "高频细节增强，人声和乐器更明亮清晰"),
        ("混响强度", 0, 100, "添加轻微的空间混响（早期反射），声音更有临场感"),
        ("环绕强度", 0, 100, "立体声加宽，戴耳机时更有包围感"),
        ("超重低音", 0, 100, "110 Hz 以下低频增强（最多 +10 dB），低音更有弹性"),
        ("动态推进", 0, 100, "压缩动态范围并补偿增益，节奏更紧凑有冲击力"),
        ("声道平衡", -100, 100, "左右声道输出平衡（负数偏左，正数偏右）"),
    };

    public SoundEffectWindow(AppSettings settings, IntPtr ownerHwnd, Action applyToPlayback)
    {
        _ownerHwnd = ownerHwnd;
        _settings = settings;
        _applyToPlayback = applyToPlayback;

        InitializeComponent();
        Root.RequestedTheme = settings.ThemeMode == "Light" ? ElementTheme.Light : ElementTheme.Dark;

        SoundFx.LoadFrom(_settings);

        _commitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _commitTimer.Tick += (_, _) =>
        {
            _commitTimer.Stop();
            SoundFx.SaveTo(_settings);
            SettingsStore.Save(_settings);
            _applyToPlayback();
        };

        BuildPresetChips();
        BuildEqPanel();
        BuildFxGrid();

        _suppress = true;
        EnabledToggle.IsOn = SoundFx.Enabled;
        EqDisabledHint.Visibility = SoundFx.Enabled ? Visibility.Collapsed : Visibility.Visible;
        ApplyToControls();
        RefreshPresetSelection();
        _suppress = false;

        ConfigureWindow();
    }

    private void ConfigureWindow()
    {
        try
        {
            var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "AppIcon.ico");
            if (System.IO.File.Exists(icon))
                AppWindow.SetIcon(icon);
        }
        catch
        {
            // cosmetic only
        }

        var w = _settings.SoundFxW is >= 620 and <= 4000 ? _settings.SoundFxW : 720;
        var h = _settings.SoundFxH is >= 500 and <= 4000 ? _settings.SoundFxH : 660;
        AppWindow.ResizeClient(new SizeInt32(w, h));
        _frameW = AppWindow.Size.Width - w;
        _frameH = AppWindow.Size.Height - h;
        CenterOnOwner(w + _frameW, h + _frameH);

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = 620;
            p.PreferredMinimumHeight = 500;
        }

        var dark = Root.RequestedTheme != ElementTheme.Light;
        var tb = AppWindow.TitleBar;
        var bg = dark ? 0x0b0b11u : 0xf3f3f6u;
        var fg = dark ? 0xf2f2f5u : 0x16161cu;
        var hover = dark ? 0x23232fu : 0xe8e8efu;
        var press = dark ? 0x17171fu : 0xdcdce5u;
        tb.BackgroundColor = tb.ButtonBackgroundColor = tb.ButtonInactiveBackgroundColor = Rgb(bg);
        tb.ForegroundColor = tb.ButtonForegroundColor = tb.ButtonInactiveForegroundColor = Rgb(fg);
        tb.ButtonHoverBackgroundColor = Rgb(hover);
        tb.ButtonPressedBackgroundColor = Rgb(press);

        Closed += (_, _) =>
        {
            // Remember the CLIENT size (see OnlineLyricWindow: AppWindow.Size
            // includes the title bar, restore goes through ResizeClient).
            var size = AppWindow.Size;
            var cw = size.Width - _frameW;
            var ch = size.Height - _frameH;
            if (cw >= 620 && ch >= 500)
            {
                _settings.SoundFxW = cw;
                _settings.SoundFxH = ch;
                SettingsStore.Save(_settings);
            }
            _commitTimer.Stop();
        };
    }

    private static Windows.UI.Color Rgb(uint v) => Windows.UI.Color.FromArgb(
        255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private void CenterOnOwner(int windowW, int windowH)
    {
        var or = new RECT();
        var ok = _ownerHwnd != IntPtr.Zero && GetWindowRect(_ownerHwnd, ref or);
        if (!ok || or.L < -30000 || or.R - or.L <= 0)
        {
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            AppWindow.Move(new PointInt32((sw - windowW) / 2, (sh - windowH) / 2));
            return;
        }

        int cx = or.L + (or.R - or.L) / 2 - windowW / 2;
        int cy = or.T + (or.B - or.T) / 2 - windowH / 2;

        var mon = MonitorFromWindow(_ownerHwnd, 1);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfoW(mon, ref mi))
        {
            cx = Math.Clamp(cx, mi.Work.L, Math.Max(mi.Work.L, mi.Work.R - windowW));
            cy = Math.Clamp(cy, mi.Work.T, Math.Max(mi.Work.T, mi.Work.B - windowH));
        }
        AppWindow.Move(new PointInt32(cx, cy));
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, ref RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfoW(IntPtr h, ref MONITORINFO mi);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    private struct RECT { public int L, T, R, B; }
    private struct MONITORINFO { public int cbSize; public RECT Monitor, Work; public uint Flags; }

    // ---------- UI construction ----------

    private void BuildPresetChips()
    {
        var wrap = new SimpleWrapPanel { HorizontalSpacing = 8, VerticalSpacing = 8 };

        foreach (var (name, label, _) in SoundFx.Presets)
        {
            var btn = new ToggleButton
            {
                Content = label,
                Tag = name,
                MinWidth = 64,
                Padding = new Thickness(14, 6, 14, 6),
                CornerRadius = new CornerRadius(6),
            };
            btn.Click += PresetChip_Click;
            _presetButtons[name] = btn;
            wrap.Children.Add(btn);
        }

        // "自定义" is a status chip: it lights up when a slider was moved;
        // clicking it changes nothing.
        _customChip = new ToggleButton
        {
            Content = "自定义",
            MinWidth = 64,
            Padding = new Thickness(14, 6, 14, 6),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
        };
        wrap.Children.Add(_customChip);

        PresetPanel.Children.Add(wrap);
    }

    private void BuildEqPanel()
    {
        // Sliders use the saved accent color (QqGreen is mutated at runtime in
        // the main window's own resource dictionary, which this window cannot
        // see — build the brush the same way OnlineLyricWindow does).
        var accent = new SolidColorBrush(ParseAccent(_settings.AccentColor));
        var secondary = (Brush)Application.Current.Resources["TextSecondary"];

        for (var i = 0; i < 10; i++)
        {
            var value = new TextBlock
            {
                Text = "0.0",
                FontSize = 11,
                MinWidth = 46,
                TextAlignment = TextAlignment.Center,
                Foreground = secondary,
            };

            // WinUI's Slider has no vertical orientation — rotate it. With a
            // -90° rotation the minimum sits at the bottom, the maximum on top.
            var slider = new Slider
            {
                Width = 190,
                Height = 46,
                Minimum = -12,
                Maximum = 12,
                StepFrequency = 0.5,
                Tag = i,
                Foreground = accent,
            };
            slider.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            slider.RenderTransform = new RotateTransform { Angle = -90 };
            slider.ValueChanged += EqSlider_ValueChanged;
            _eqSliders[i] = slider;

            var freq = new TextBlock
            {
                Text = SoundFx.BandLabels[i],
                FontSize = 11,
                MinWidth = 46,
                TextAlignment = TextAlignment.Center,
                Foreground = secondary,
            };

            var host = new Grid { Width = 46, Height = 200 };
            host.Children.Add(slider);

            var column = new StackPanel { Spacing = 4 };
            column.Children.Add(value);
            column.Children.Add(host);
            column.Children.Add(freq);

            EqPanel.Children.Add(column);
        }
    }

    private void BuildFxGrid()
    {
        var accent = new SolidColorBrush(ParseAccent(_settings.AccentColor));

        for (var i = 0; i < EffectDefs.Length; i++)
        {
            var (label, min, max, tip) = EffectDefs[i];

            var header = new Grid { ColumnSpacing = 8 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lbl = new TextBlock
            {
                Text = label,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextPrimary"],
                VerticalAlignment = VerticalAlignment.Center,
            };
            var val = new TextBlock
            {
                Text = "0",
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextSecondary"],
            };
            header.Children.Add(lbl);
            Grid.SetColumn(val, 1);
            header.Children.Add(val);

            var slider = new Slider
            {
                Minimum = min,
                Maximum = max,
                StepFrequency = 1,
                Tag = i,
                MinWidth = 180,
                Foreground = accent,
            };
            ToolTipService.SetToolTip(slider, tip);
            slider.ValueChanged += FxSlider_ValueChanged;

            var cell = new StackPanel { Spacing = 4 };
            cell.Children.Add(header);
            cell.Children.Add(slider);

            var row = i / 2;
            var col = i % 2;
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, col);
            FxGrid.Children.Add(cell);
        }
    }

    // ---------- state ↔ controls ----------

    private void ApplyToControls()
    {
        for (var i = 0; i < 10; i++)
        {
            _eqSliders[i].Value = SoundFx.EqGains[i];
            _eqValues[i].Text = FormatDb(SoundFx.EqGains[i]);
        }

        var fx = FxGrid.Children;
        for (var i = 0; i < EffectDefs.Length && i * 1 < fx.Count; i++)
        {
            if (fx[i] is StackPanel cell && cell.Children.Count >= 2 && cell.Children[1] is Slider s)
            {
                s.Value = i switch
                {
                    0 => SoundFx.HiFi,
                    1 => SoundFx.Reverb,
                    2 => SoundFx.Surround,
                    3 => SoundFx.BassBoost,
                    4 => SoundFx.Punch,
                    _ => SoundFx.Balance,
                };
                if (cell.Children[0] is Grid header && header.Children.Count >= 2 && header.Children[1] is TextBlock t)
                    t.Text = FormatFx(i, s.Value);
            }
        }
    }

    private void RefreshPresetSelection()
    {
        foreach (var (name, _, _) in SoundFx.Presets)
        {
            if (_presetButtons.TryGetValue(name, out var btn))
                btn.IsChecked = name == SoundFx.PresetName;
        }
        if (_customChip != null)
            _customChip.IsChecked = SoundFx.PresetName == "Custom";
    }

    // ---------- events ----------

    private void EnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;
        SoundFx.Enabled = EnabledToggle.IsOn;
        EqDisabledHint.Visibility = SoundFx.Enabled ? Visibility.Collapsed : Visibility.Visible;
        Commit();
    }

    private void PresetChip_Click(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;
        if (sender is not ToggleButton btn || btn.Tag is not string name)
            return;

        var preset = Array.Find(SoundFx.Presets, p => p.Name == name);
        if (preset.Gains == null)
            return;

        _suppress = true;
        for (var i = 0; i < 10; i++)
        {
            SoundFx.EqGains[i] = preset.Gains[i];
            _eqSliders[i].Value = preset.Gains[i];
            _eqValues[i].Text = FormatDb(preset.Gains[i]);
        }
        SoundFx.PresetName = name;
        RefreshPresetSelection();
        _suppress = false;

        Commit();
    }

    private void EqSlider_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;
        if (sender is not Slider s || s.Tag is not int i)
            return;

        var v = Math.Round(s.Value * 2, MidpointRounding.AwayFromZero) / 2;
        SoundFx.EqGains[i] = v;
        _eqValues[i].Text = FormatDb(v);
        SoundFx.PresetName = "Custom";
        RefreshPresetSelection();
        Commit();
    }

    private void FxSlider_ValueChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress)
            return;
        if (sender is not Slider s || s.Tag is not int i)
            return;

        switch (i)
        {
            case 0: SoundFx.HiFi = s.Value; break;
            case 1: SoundFx.Reverb = s.Value; break;
            case 2: SoundFx.Surround = s.Value; break;
            case 3: SoundFx.BassBoost = s.Value; break;
            case 4: SoundFx.Punch = s.Value; break;
            default: SoundFx.Balance = s.Value; break;
        }

        if (FxGrid.Children[i] is StackPanel cell && cell.Children.Count >= 1
            && cell.Children[0] is Grid header && header.Children.Count >= 2 && header.Children[1] is TextBlock t)
            t.Text = FormatFx(i, s.Value);

        Commit();
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        _suppress = true;
        for (var i = 0; i < 10; i++)
        {
            SoundFx.EqGains[i] = 0;
            _eqSliders[i].Value = 0;
            _eqValues[i].Text = FormatDb(0);
        }
        SoundFx.HiFi = SoundFx.Reverb = SoundFx.Surround = SoundFx.BassBoost = SoundFx.Punch = SoundFx.Balance = 0;
        SoundFx.PresetName = "Off";
        ApplyToControls();
        RefreshPresetSelection();
        _suppress = false;
        Commit();
    }

    // ---------- commit ----------

    private void Commit()
    {
        // Coalesce slider bursts into one settings write + one reload request.
        _commitTimer.Stop();
        _commitTimer.Start();
    }

    private static string FormatDb(double v) =>
        v.ToString("+0.#;-0.#;0.0", CultureInfo.InvariantCulture);

    /// <summary>Parse "#RRGGBB"/"#AARRGGBB", falling back to the default accent.</summary>
    private static Windows.UI.Color ParseAccent(string? hex)
    {
        try
        {
            var s = (hex ?? "").TrimStart('#');
            if (s.Length == 8)
                return Microsoft.UI.ColorHelper.FromArgb(
                    Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16), Convert.ToByte(s.Substring(6, 2), 16));
            if (s.Length == 6)
                return Microsoft.UI.ColorHelper.FromArgb(255,
                    Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16),
                    Convert.ToByte(s.Substring(4, 2), 16));
        }
        catch
        {
            // fall through to default
        }
        return Microsoft.UI.ColorHelper.FromArgb(255, 0x31, 0xc2, 0x7c);
    }

    private static string FormatFx(int i, double v)
    {
        if (i == 5) // balance
        {
            var r = Math.Round(v);
            if (r == 0) return "居中";
            return r < 0 ? $"左 {Math.Abs(r) / 100.0:0%}" : $"右 {r / 100.0:0%}";
        }
        return Math.Round(v).ToString(CultureInfo.InvariantCulture) + "%";
    }
}

/// <summary>
/// Minimal flow-layout panel — WinUI 3 ships no WrapPanel, and the preset
/// chips must wrap when the window is narrow.
/// </summary>
internal sealed class SimpleWrapPanel : Panel
{
    public double HorizontalSpacing { get; set; } = 8;
    public double VerticalSpacing { get; set; } = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = double.IsFinite(availableSize.Width) ? availableSize.Width : double.PositiveInfinity;
        double x = 0, y = 0, lineH = 0, widest = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(maxWidth, double.PositiveInfinity));
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            if (x > 0 && x + w > maxWidth)
            {
                x = 0;
                y += lineH + VerticalSpacing;
                lineH = 0;
            }
            x += w + HorizontalSpacing;
            lineH = Math.Max(lineH, h);
            widest = Math.Max(widest, Math.Min(x, maxWidth));
        }

        return new Size(widest, y + lineH);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, lineH = 0;

        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;
            if (x > 0 && x + w > finalSize.Width)
            {
                x = 0;
                y += lineH + VerticalSpacing;
                lineH = 0;
            }
            child.Arrange(new Rect(x, y, w, h));
            x += w + HorizontalSpacing;
            lineH = Math.Max(lineH, h);
        }

        return finalSize;
    }
}
