using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class MusicalTimeTests
{
    [Theory]
    [InlineData(0.0, "1.1.1")]
    [InlineData(0.25, "1.1.2")]
    [InlineData(1.0, "1.2.1")]
    [InlineData(4.0, "2.1.1")]
    [InlineData(9.0, "3.2.1")]
    [InlineData(18.0, "5.3.1")]
    [InlineData(3.99, "1.4.4")]
    [InlineData(-2.0, "1.1.1")]
    public void FormatPosition_IsBarBeatStep(double beats, string expected) =>
        Assert.Equal(expected, MusicalTime.FormatPosition(beats));

    [Fact]
    public void FormatPosition_ToleratesFloatingPointJustBelowAStep() =>
        Assert.Equal("2.1.1", MusicalTime.FormatPosition(4.0 - 1e-9));

    [Theory]
    [InlineData(120.0, 500.0)]
    [InlineData(60.0, 1000.0)]
    [InlineData(90.0, 666.6666666)]
    public void MsPerBeat_FollowsTheTempo(double bpm, double expected) =>
        Assert.Equal(expected, MusicalTime.MsPerBeat(bpm), 4);

    [Fact]
    public void BeatsAndMs_RoundTrip()
    {
        Assert.Equal(3.0, MusicalTime.MsToBeats(1500, 120), 9);
        Assert.Equal(1500.0, MusicalTime.BeatsToMs(3, 120), 9);
    }

    [Theory]
    [InlineData(20.0, 40.0)]
    [InlineData(300.0, 240.0)]
    [InlineData(119.6, 120.0)]
    [InlineData(double.NaN, 120.0)]
    public void ClampBpm_KeepsWholeTempoInRange(double bpm, double expected) =>
        Assert.Equal(expected, MusicalTime.ClampBpm(bpm));
}

public class GridDivisionTests
{
    [Fact]
    public void All_IsInMenuOrder_WithLabels()
    {
        Assert.Equal(new[] { "Bar", "1/2", "1/4 (beat)", "1/8", "1/16", "1/32", "1/8T", "1/16T", "Off" },
            GridDivisions.All.Select(GridDivisions.Label));
        Assert.Equal(GridDivision.Sixteenth, GridDivisions.Default);
    }

    [Theory]
    [InlineData(GridDivision.Bar, 4.0)]
    [InlineData(GridDivision.Quarter, 1.0)]
    [InlineData(GridDivision.Sixteenth, 0.25)]
    [InlineData(GridDivision.ThirtySecond, 0.125)]
    [InlineData(GridDivision.EighthTriplet, 1.0 / 3.0)]
    [InlineData(GridDivision.SixteenthTriplet, 1.0 / 6.0)]
    public void LengthBeats_MatchesTheDivision(GridDivision division, double beats) =>
        Assert.Equal(beats, GridDivisions.LengthBeats(division)!.Value, 9);

    [Fact]
    public void Off_HasNoGridButDrawsSixteenths()
    {
        Assert.Null(GridDivisions.LengthBeats(GridDivision.Off));
        Assert.Equal(0.25, GridDivisions.NoteLengthBeats(GridDivision.Off));
        Assert.Equal(1.37, GridDivisions.Snap(1.37, GridDivision.Off));
    }

    [Theory]
    [InlineData(1.37, GridDivision.Sixteenth, 1.25)]
    [InlineData(1.40, GridDivision.Sixteenth, 1.5)]
    [InlineData(5.9, GridDivision.Bar, 4.0)]
    [InlineData(6.1, GridDivision.Bar, 8.0)]
    [InlineData(0.30, GridDivision.EighthTriplet, 1.0 / 3.0)]
    public void Snap_GoesToTheNearestLine(double beats, GridDivision division, double expected) =>
        Assert.Equal(expected, GridDivisions.Snap(beats, division), 9);

    [Fact]
    public void Snap_Disabled_LeavesThePositionAlone() =>
        Assert.Equal(1.37, GridDivisions.Snap(1.37, GridDivision.Sixteenth, enabled: false));

    [Fact]
    public void SnapDown_GoesToTheLineAtOrBefore()
    {
        Assert.Equal(1.25, GridDivisions.SnapDown(1.49, GridDivision.Sixteenth), 9);
        Assert.Equal(1.5, GridDivisions.SnapDown(1.5 - 1e-12, GridDivision.Sixteenth), 9);
    }

    [Theory]
    [InlineData("EighthTriplet", GridDivision.EighthTriplet)]
    [InlineData(null, GridDivision.Sixteenth)]
    [InlineData("nonsense", GridDivision.Sixteenth)]
    [InlineData("42", GridDivision.Sixteenth)]
    public void Parse_FallsBackToTheDefault(string? name, GridDivision expected) =>
        Assert.Equal(expected, GridDivisions.Parse(name));
}
