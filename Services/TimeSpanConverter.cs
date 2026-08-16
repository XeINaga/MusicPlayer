using System;
using Microsoft.UI.Xaml.Data;

namespace MusicPlayer.Services;

/// <summary>
/// Converts a <see cref="TimeSpan"/> to "m:ss" for playlist rows.
/// Returns an empty string for zero / unset durations.
/// </summary>
public sealed class TimeSpanConverter : IValueConverter
{
    public object? Convert(object? value, Type typeName, object? parameter, string? language)
    {
        if (value is TimeSpan ts && ts.TotalSeconds > 0)
        {
            var total = (int)ts.TotalSeconds;
            if (total >= 3600)
                return $"{total / 3600}:{total / 60 % 60:D2}:{total % 60:D2}";
            return $"{total / 60}:{total % 60:D2}";
        }

        return string.Empty;
    }

    public object? ConvertBack(object? value, Type typeName, object? parameter, string? language) => null;
}
