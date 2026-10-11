using ToneDetector.App.Converters;
using Xunit;

namespace ToneDetector.Tests;

public class ConfidenceToOpacityConverterTests
{
    private readonly ConfidenceToOpacityConverter _converter = new();

    [Theory]
    [InlineData(0.0, 0.3)]
    [InlineData(0.5, 0.65)]
    [InlineData(1.0, 1.0)]
    [InlineData(-5.0, 0.3)] // clamped
    [InlineData(5.0, 1.0)] // clamped
    public void Convert_MapsConfidenceToExpectedOpacity(double confidence, double expectedOpacity)
    {
        object? result = _converter.Convert(confidence, typeof(double), null!, null!);

        Assert.Equal(expectedOpacity, (double)result!, precision: 6);
    }

    [Fact]
    public void Convert_OnNonDoubleValue_ReturnsFullOpacity()
    {
        object? result = _converter.Convert("not a number", typeof(double), null!, null!);

        Assert.Equal(1.0, (double)result!);
    }
}
