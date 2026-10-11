using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ToneDetector.App.Converters;

/// <summary>Width (double) → Visible when at least the minimum given as ConverterParameter
/// (invariant number), otherwise Collapsed. Used to drop the header's device block once its slot
/// is too narrow to show a useful part of the device name.</summary>
public sealed class MinWidthToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double minWidth = parameter switch
        {
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => 0.0,
        };
        return value is double width && width >= minWidth ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
