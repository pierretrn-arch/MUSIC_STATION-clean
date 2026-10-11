using ToneDetector.Core.Dsp;
using Xunit;

namespace ToneDetector.Tests;

public class DspStageTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void LowPassFilter_AttenuatesFrequencyWellAboveCutoff()
    {
        var filter = new LowPassFilter(cutoffHz: 500, sampleRate: SampleRate);
        float[] lowTone = GenerateSineWave(100, SampleRate, 0.05);
        float[] highTone = GenerateSineWave(8000, SampleRate, 0.05);

        double lowOutputRms = ProcessAndMeasureRms(filter, lowTone);
        filter.Reset();
        double highOutputRms = ProcessAndMeasureRms(filter, highTone);

        Assert.True(highOutputRms < lowOutputRms * 0.2,
            $"Expected 8kHz tone to be strongly attenuated relative to 100Hz tone through a 500Hz low-pass filter. low={lowOutputRms:F4} high={highOutputRms:F4}");
    }

    [Fact]
    public void HighPassFilter_AttenuatesFrequencyWellBelowCutoff()
    {
        var filter = new HighPassFilter(cutoffHz: 500, sampleRate: SampleRate);
        float[] lowTone = GenerateSineWave(50, SampleRate, 0.05);
        float[] highTone = GenerateSineWave(4000, SampleRate, 0.05);

        double lowOutputRms = ProcessAndMeasureRms(filter, lowTone);
        filter.Reset();
        double highOutputRms = ProcessAndMeasureRms(filter, highTone);

        Assert.True(lowOutputRms < highOutputRms * 0.2,
            $"Expected 50Hz tone to be strongly attenuated relative to 4kHz tone through a 500Hz high-pass filter. low={lowOutputRms:F4} high={highOutputRms:F4}");
    }

    [Fact]
    public void Resampler_ChangingSampleRate_PreservesApproximateDuration()
    {
        var resampler = new Resampler();
        float[] input = GenerateSineWave(440, 48000, 1.0);

        float[] output = resampler.Resample(input, 48000, 16000);

        double expectedLength = input.Length * (16000.0 / 48000.0);
        Assert.True(Math.Abs(output.Length - expectedLength) <= 1);
    }

    [Fact]
    public void MovingAverageSmoother_OnConstantInput_ConvergesToThatValue()
    {
        var smoother = new MovingAverageSmoother(windowSize: 5);
        double result = 0;
        for (int i = 0; i < 10; i++)
            result = smoother.Process(6900.0);

        Assert.Equal(6900.0, result, precision: 6);
    }

    [Fact]
    public void MovingAverageSmoother_SmoothsOutASingleOutlier()
    {
        var smoother = new MovingAverageSmoother(windowSize: 5);
        double result = 0;
        for (int i = 0; i < 5; i++)
            result = smoother.Process(6900.0);

        result = smoother.Process(7100.0); // one outlier spike (+200 cents)

        Assert.True(Math.Abs(result - 6900.0) < 50.0,
            $"Expected the outlier to be substantially smoothed out, got {result}");
    }

    [Fact]
    public void KalmanPitchSmoother_OnConstantInput_ConvergesToThatValue()
    {
        var smoother = new KalmanPitchSmoother(KalmanPitchSmoother.Preset.Smooth);
        double result = 0;
        for (int i = 0; i < 50; i++)
            result = smoother.Process(6900.0);

        Assert.True(Math.Abs(result - 6900.0) < 1e-6);
    }

    [Fact]
    public void KalmanPitchSmoother_Reset_ClearsState()
    {
        var smoother = new KalmanPitchSmoother();
        for (int i = 0; i < 20; i++)
            smoother.Process(6900.0);

        smoother.Reset();
        double firstAfterReset = smoother.Process(4800.0);

        Assert.Equal(4800.0, firstAfterReset, precision: 6);
    }

    private static double ProcessAndMeasureRms(IAudioStage stage, float[] samples)
    {
        var buffer = (float[])samples.Clone();
        stage.Process(buffer);

        // Discard the first half (filter settling time) before measuring steady-state RMS.
        int start = buffer.Length / 2;
        double sumSquares = 0.0;
        for (int i = start; i < buffer.Length; i++)
            sumSquares += (double)buffer[i] * buffer[i];
        return Math.Sqrt(sumSquares / (buffer.Length - start));
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
