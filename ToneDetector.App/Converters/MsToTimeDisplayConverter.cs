using System.Globalization;
using System.Windows.Data;

namespace ToneDetector.App.Converters;

/// <summary>Formats a millisecond double as mm:ss.fff - used by the transport control's
/// position/duration display. See <see cref="TimeFormat"/> for the shared format itself.</summary>
public sealed class MsToTimeDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double milliseconds ? TimeFormat.MillisecondsToDisplay(milliseconds) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
