using ToneDetector.Core.Pitch;
using Xunit;

namespace ToneDetector.Tests;

public class YinPitchDetectorTests
{
    private const int SampleRate = 48000;

    [Theory]
    [InlineData(82.41)]   // E2 - low guitar string
    [InlineData(110.00)]  // A2
    [InlineData(220.00)]  // A3
    [InlineData(440.00)]  // A4 concert pitch
    [InlineData(880.00)]  // A5
    [InlineData(1046.50)] // C6
    public void DetectPitch_OnPureSineTone_IsAccurateWithinFiveCents(double frequencyHz)
    {
        var detector = new YinPitchDetector { MinFrequencyHz = 50, MaxFrequencyHz = 1500 };
        float[] frame = GenerateSineWave(frequencyHz, SampleRate, durationSeconds: 0.35);

        PitchFrameResult result = detector.DetectPitch(frame, SampleRate);

        Assert.True(result.IsVoiced, $"Expected {frequencyHz} Hz tone to be detected as voiced.");
        double centsError = 1200.0 * Math.Log2(result.F0Hz / frequencyHz);
        Assert.True(Math.Abs(centsError) < 5.0,
            $"Expected cents error < 5 for {frequencyHz} Hz, got {centsError:F2} cents (detected {result.F0Hz:F3} Hz).");
    }

    [Fact]
    public void DetectPitch_OnSilence_IsUnvoiced()
    {
        var detector = new YinPitchDetector();
        float[] frame = new float[8192]; // all zeros

        PitchFrameResult result = detector.DetectPitch(frame, SampleRate);

        Assert.False(result.IsVoiced);
    }

    [Fact]
    public void DetectPitch_OnWhiteNoise_IsUnvoicedOrLowConfidence()
    {
        var detector = new YinPitchDetector();
        var random = new Random(42);
        float[] frame = new float[8192];
        for (int i = 0; i < frame.Length; i++)
            frame[i] = (float)(random.NextDouble() * 2.0 - 1.0);

        PitchFrameResult result = detector.DetectPitch(frame, SampleRate);

        Assert.False(result.IsVoiced);
    }

    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, double durationSeconds)
    {
        int sampleCount = (int)(sampleRate * durationSeconds);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate);
        return samples;
    }
}
