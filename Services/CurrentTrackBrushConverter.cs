using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MusicPlayer.Services;

/// <summary>
/// True → the brush passed as ConverterParameter (the accent brush), false →
/// <see cref="Fallback"/> — the theme-aware TextPrimary brush the main window
/// injects on startup / theme switch. UnsetValue or null is NOT usable here:
/// in this host the template TextBlock's default foreground resolves to white,
/// which is invisible on the light theme.
/// </summary>
public sealed partial class CurrentTrackBrushConverter : IValueConverter
{
    /// <summary>Theme-aware default foreground, set by MainWindow.ApplyThemeMode.</summary>
    public static Brush? Fallback { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is true && parameter is Brush accent)
            return accent;
        return Fallback ?? DependencyProperty.UnsetValue;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
