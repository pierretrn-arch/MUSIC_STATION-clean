using ToneDetector.App.Controls;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Pure helpers behind the piano roll's key column and ruler styling (Design/KeyColumn.dc.html).</summary>
public class PianoRollControlStyleTests
{
    [Theory]
    [InlineData(60, "C4")]
    [InlineData(61, "C#4")]
    [InlineData(64, "E4")]
    [InlineData(70, "A#4")]
    [InlineData(36, "C2")]
    public void KeyLabel_NamesEveryKeyWithHashForSharps(int midi, string expected)
    {
        Assert.Equal(expected, PianoRollControl.KeyLabel(midi));
    }

    [Theory]
    [InlineData(72.0, 46.0)] // the design: 26px shorter
    [InlineData(52.0, 26.0)]
    [InlineData(40.0, 25.6)] // narrow column: proportionally shorter
    public void BlackKeyWidth_Is26pxShorter_OrProportionalWhenNarrow(double column, double expected)
    {
        Assert.Equal(expected, PianoRollControl.BlackKeyWidth(column), precision: 6);
    }

    [Theory]
    [InlineData(240.0, 1)] // the design: 60px a beat, every bar labelled
    [InlineData(36.0, 1)]
    [InlineData(30.0, 2)]
    [InlineData(10.0, 4)]
    [InlineData(4.0, 16)]
    public void BarLabelStep_ThinsBarNumbersWhenZoomedOut(double pxPerBar, int expected)
    {
        Assert.Equal(expected, PianoRollControl.BarLabelStep(pxPerBar));
    }

    [Theory]
    [InlineData(24.0, 3.0)]  // the design: 3px above and below in a 24px row
    [InlineData(16.0, 2.4)]
    [InlineData(80.0, 3.0)]
    public void BarInset_IsThreePixelsAtMost(double rowHeight, double expected)
    {
        Assert.Equal(expected, PianoRollControl.BarInset(rowHeight), precision: 6);
    }
}
