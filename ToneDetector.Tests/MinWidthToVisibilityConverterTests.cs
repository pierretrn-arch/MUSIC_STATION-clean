using System.Globalization;
using System.Windows;
using ToneDetector.App.Converters;
using Xunit;

namespace ToneDetector.Tests;

public class MinWidthToVisibilityConverterTests
{
    private static object Convert(object value, object parameter) =>
        new MinWidthToVisibilityConverter().Convert(value, typeof(Visibility), parameter, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(182.0, Visibility.Visible)]
    [InlineData(500.0, Visibility.Visible)]
    [InlineData(181.9, Visibility.Collapsed)]
    [InlineData(0.0, Visibility.Collapsed)]
    public void ComparesTheWidthWithTheParameter(double width, Visibility expected)
    {
        Assert.Equal(expected, Convert(width, "182"));
    }

    [Fact]
    public void ParsesTheParameterInvariantly()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("fr-FR"); // comma decimal separator
            Assert.Equal(Visibility.Collapsed, Convert(182.4, "182.5"));
            Assert.Equal(Visibility.Visible, Convert(182.6, "182.5"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void AcceptsADoubleParameter()
    {
        Assert.Equal(Visibility.Visible, Convert(200.0, 182.0));
    }

    [Fact]
    public void NonNumericWidth_IsCollapsed()
    {
        Assert.Equal(Visibility.Collapsed, Convert("wide", "182"));
    }
}
