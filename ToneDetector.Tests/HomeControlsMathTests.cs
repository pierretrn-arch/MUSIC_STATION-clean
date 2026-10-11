using System.Windows;
using ToneDetector.App.Controls;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Position maths behind the Home <see cref="TuningMeter"/> and <see cref="PitchHistoryChart"/>.
/// The meter reproduces the mockup's 1000-wide scale: −50 at 20, 0 at 500, +50 at 980, with 9.6
/// px per cent.</summary>
public class HomeControlsMathTests
{
    [Theory]
    [InlineData(-50.0, 20.0)]
    [InlineData(-25.0, 260.0)]
    [InlineData(-5.0, 452.0)]
    [InlineData(0.0, 500.0)]
    [InlineData(2.0, 519.2)]
    [InlineData(5.0, 548.0)]
    [InlineData(50.0, 980.0)]
    public void TuningMeter_CentsToX_MatchesTheMockupScale(double cents, double expectedX)
    {
        Assert.Equal(expectedX, TuningMeter.CentsToX(cents, 1000), precision: 6);
    }

    [Theory]
    [InlineData(-80.0, 20.0)]
    [InlineData(120.0, 980.0)]
    public void TuningMeter_CentsToX_ClampsBeyondTheScale(double cents, double expectedX)
    {
        Assert.Equal(expectedX, TuningMeter.CentsToX(cents, 1000), precision: 6);
    }

    [Fact]
    public void TuningMeter_CentsToX_ScalesWithWidth()
    {
        Assert.Equal(250.0, TuningMeter.CentsToX(0, 500), precision: 6);
        Assert.Equal(10.0, TuningMeter.CentsToX(-50, 500), precision: 6);
    }

    [Theory]
    [InlineData(-50.0, "−50")]
    [InlineData(-25.0, "−25")]
    [InlineData(0.0, "0")]
    [InlineData(25.0, "+25")]
    [InlineData(50.0, "+50")]
    public void TuningMeter_TickLabels_UseARealMinusSign(double cents, string expected)
    {
        Assert.Equal(expected, TuningMeter.FormatTickLabel(cents));
    }

    [Fact]
    public void PitchHistoryChart_NewestSampleSitsOnTheRightEdge()
    {
        Point p = PitchHistoryChart.MapPoint(timeMs: 5000, cents: 0, latestMs: 5000, windowMs: 5000, width: 1000, height: 84);

        Assert.Equal(1000.0, p.X, precision: 6);
        Assert.Equal(42.0, p.Y, precision: 6);
    }

    [Fact]
    public void PitchHistoryChart_OldestSampleSitsOnTheLeftEdge()
    {
        Point p = PitchHistoryChart.MapPoint(timeMs: 0, cents: 0, latestMs: 5000, windowMs: 5000, width: 1000, height: 84);

        Assert.Equal(0.0, p.X, precision: 6);
    }

    [Fact]
    public void PitchHistoryChart_SharpIsUp_FlatIsDown_ClampedInside()
    {
        Point sharp = PitchHistoryChart.MapPoint(5000, 50, 5000, 5000, 1000, 84);
        Point flat = PitchHistoryChart.MapPoint(5000, -50, 5000, 5000, 1000, 84);
        Point wayOff = PitchHistoryChart.MapPoint(5000, 400, 5000, 5000, 1000, 84);

        Assert.True(sharp.Y < 42 && sharp.Y >= 0);
        Assert.True(flat.Y > 42 && flat.Y <= 84);
        Assert.Equal(sharp.Y, wayOff.Y, precision: 6);
    }

    [Fact]
    public void PitchHistoryChart_SamplesOlderThanTheWindowClampToTheLeftEdge()
    {
        Point p = PitchHistoryChart.MapPoint(timeMs: -2000, cents: 0, latestMs: 5000, windowMs: 5000, width: 1000, height: 84);

        Assert.Equal(0.0, p.X, precision: 6);
    }
}
