using ToneDetector.Core.Analysis;
using Xunit;

namespace ToneDetector.Tests;

public class TempoDetectorTests
{
    [Fact]
    public void Detect_OnExactly500msIntervals_Returns120BpmWithFullConfidence()
    {
        var detector = new TempoDetector();
        AddOnsets(detector, startMs: 0, count: 8, intervalMs: 500);

        DetectedTempo? tempo = detector.Detect();

        Assert.NotNull(tempo);
        Assert.Equal(120.0, tempo!.Value.Bpm, precision: 3);
        Assert.Equal(1.0, tempo.Value.Confidence, precision: 6);
    }

    [Fact]
    public void Detect_With500msIntervalsPlusJitter_ReturnsBpmNear120()
    {
        var detector = new TempoDetector();
        double[] intervalsMs = { 480, 500, 510, 495, 505, 500, 520 };
        double onset = 0;
        detector.AddNoteOnset(onset);
        foreach (double interval in intervalsMs)
        {
            onset += interval;
            detector.AddNoteOnset(onset);
        }

        DetectedTempo? tempo = detector.Detect();

        Assert.NotNull(tempo);
        Assert.InRange(tempo!.Value.Bpm, 115.0, 125.0);
    }

    [Fact]
    public void Detect_On250msIntervals_DoublesPeriodTo120Bpm()
    {
        var detector = new TempoDetector();
        AddOnsets(detector, startMs: 0, count: 8, intervalMs: 250);

        DetectedTempo? tempo = detector.Detect();

        Assert.NotNull(tempo);
        Assert.Equal(120.0, tempo!.Value.Bpm, precision: 3);
    }

    [Fact]
    public void Detect_WithFewerThanMinimumIois_ReturnsNull()
    {
        var detector = new TempoDetector();
        AddOnsets(detector, startMs: 0, count: 5, intervalMs: 500); // 4 IOIs

        Assert.Null(detector.Detect());
    }

    [Fact]
    public void Reset_ClearsAccumulatedIntervals()
    {
        var detector = new TempoDetector();
        AddOnsets(detector, startMs: 0, count: 8, intervalMs: 500);

        detector.Reset();

        Assert.Null(detector.Detect());
    }

    private static void AddOnsets(TempoDetector detector, double startMs, int count, double intervalMs)
    {
        for (int i = 0; i < count; i++)
            detector.AddNoteOnset(startMs + i * intervalMs);
    }
}
