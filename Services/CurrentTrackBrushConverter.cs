using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace MusicPlayer.Services;

/// <summary>
/// True → the brush passed as ConverterParameter (the accent brush), false →
/// null (inherits the default foreground). Marks the currently playing row in
/// the track lists without needing a per-theme resource lookup in code.
/// </summary>
public sealed partial class CurrentTrackBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true && parameter is Brush accent ? accent : null!;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
