using System.Globalization;
using System.Windows.Data;

namespace ToneDetector.App.Converters;

/// <summary>Maps a 0..1 confidence value to a 0.3..1.0 opacity, so low-confidence transcript
/// rows read as visibly dimmer without disappearing entirely.</summary>
public sealed class ConfidenceToOpacityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double confidence)
            return 1.0;

        return 0.3 + 0.7 * Math.Clamp(confidence, 0.0, 1.0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
