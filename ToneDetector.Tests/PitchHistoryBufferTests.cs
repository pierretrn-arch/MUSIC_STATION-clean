using ToneDetector.App.ViewModels;
using Xunit;

namespace ToneDetector.Tests;

public class PitchHistoryBufferTests
{
    [Fact]
    public void Constructor_RejectsNonPositiveWindow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PitchHistoryBuffer(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PitchHistoryBuffer(-1));
    }

    [Fact]
    public void Add_KeepsSamplesInOrder()
    {
        var buffer = new PitchHistoryBuffer(5000);

        buffer.Add(0, 1.0);
        buffer.Add(20, null);
        buffer.Add(40, -2.0);

        Assert.Equal(new[] { 0.0, 20.0, 40.0 }, buffer.Samples.Select(s => s.TimeMs));
        Assert.Equal(40.0, buffer.LatestTimeMs);
    }

    [Fact]
    public void Add_DropsSamplesOlderThanTheWindow()
    {
        var buffer = new PitchHistoryBuffer(5000);
        buffer.Add(0, 1.0);
        buffer.Add(1000, 2.0);
        buffer.Add(5000, 3.0); // 0 is exactly 5000 old: kept

        Assert.Equal(3, buffer.Samples.Count);

        buffer.Add(5500, 4.0); // 0 is now 5500 old: dropped; 1000 is 4500 old: kept

        Assert.Equal(new[] { 1000.0, 5000.0, 5500.0 }, buffer.Samples.Select(s => s.TimeMs));
    }

    [Fact]
    public void Add_AfterALongGap_KeepsOnlyTheNewSample()
    {
        var buffer = new PitchHistoryBuffer(5000);
        buffer.Add(0, 1.0);
        buffer.Add(10, 1.0);

        buffer.Add(60_000, 2.0);

        Assert.Single(buffer.Samples);
    }

    [Fact]
    public void Changed_FiresOnAddAndOnClearOfANonEmptyBuffer()
    {
        var buffer = new PitchHistoryBuffer(5000);
        int changes = 0;
        buffer.Changed += (_, _) => changes++;

        buffer.Clear(); // already empty: no event
        buffer.Add(0, 1.0);
        buffer.Clear();

        Assert.Equal(2, changes);
        Assert.Empty(buffer.Samples);
        Assert.Null(buffer.LatestTimeMs);
    }
}
