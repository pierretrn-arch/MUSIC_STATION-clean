using ToneDetector.Core.Analysis;
using Xunit;

namespace ToneDetector.Tests;

public class KeyDetectorTests
{
    [Fact]
    public void Detect_OnCMajorScale_ReturnsCMajorWithHighConfidence()
    {
        var detector = new KeyDetector();
        int[] cMajorScale = { 0, 2, 4, 5, 7, 9, 11, 0 }; // C D E F G A B C

        foreach (int pitchClass in cMajorScale)
            detector.AddNote(pitchClass, durationSeconds: 0.5);

        DetectedKey? key = detector.Detect();

        Assert.NotNull(key);
        Assert.Equal(0, key!.Value.Tonic);
        Assert.Equal(MusicalMode.Major, key.Value.Mode);
        Assert.True(key.Value.Confidence > 0.85, $"Expected confidence > 0.85, got {key.Value.Confidence:F3}");
    }

    [Fact]
    public void Detect_OnANaturalMinorScale_ReturnsAMinor()
    {
        var detector = new KeyDetector();
        int[] aMinorScale = { 9, 11, 0, 2, 4, 5, 7, 9 }; // A B C D E F G A

        foreach (int pitchClass in aMinorScale)
            detector.AddNote(pitchClass, durationSeconds: 0.5);

        DetectedKey? key = detector.Detect();

        Assert.NotNull(key);
        Assert.Equal(9, key!.Value.Tonic);
        Assert.Equal(MusicalMode.Minor, key.Value.Mode);
    }

    [Fact]
    public void Detect_OnUniformPitchClassDistribution_HasLowConfidence()
    {
        var detector = new KeyDetector();
        for (int pitchClass = 0; pitchClass < 12; pitchClass++)
            detector.AddNote(pitchClass, durationSeconds: 1.0);

        DetectedKey? key = detector.Detect();

        Assert.NotNull(key);
        Assert.True(key!.Value.Confidence < 0.6, $"Expected confidence < 0.6, got {key.Value.Confidence:F3}");
    }

    [Fact]
    public void Detect_WithFewerThanMinimumNotes_ReturnsNull()
    {
        var detector = new KeyDetector();
        foreach (int pitchClass in new[] { 0, 2, 4, 5 })
            detector.AddNote(pitchClass, durationSeconds: 0.5);

        Assert.Null(detector.Detect());
    }

    [Fact]
    public void Reset_ClearsAccumulatedHistogram()
    {
        var detector = new KeyDetector();
        foreach (int pitchClass in new[] { 0, 2, 4, 5, 7, 9, 11, 0 })
            detector.AddNote(pitchClass, durationSeconds: 0.5);

        detector.Reset();

        Assert.Null(detector.Detect());
    }
}
