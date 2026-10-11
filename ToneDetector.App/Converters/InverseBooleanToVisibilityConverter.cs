using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ToneDetector.App.Converters;

/// <summary>True → Collapsed, False → Visible. Used for the Piano Roll's history scrollbar,
/// which should only appear while not live (i.e. the inverse of <c>IsRecording</c>/<c>IsLive</c>).</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
