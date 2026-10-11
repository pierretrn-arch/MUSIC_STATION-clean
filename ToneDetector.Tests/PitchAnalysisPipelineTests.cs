using ToneDetector.Core.Dsp;
using ToneDetector.Core.Pipeline;
using Xunit;

namespace ToneDetector.Tests;

public class PitchAnalysisPipelineTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void ProcessFrame_OnSustainedTone_ReportsCorrectNoteAndLowCentsDeviation()
    {
        var pipeline = new PitchAnalysisPipeline();
        pipeline.PitchTrackStages.Add(new MovingAverageSmoother(windowSize: 3));

        float[] frame = GenerateSineWave(440.0, SampleRate, 0.35);
        PitchAnalysisResult result = pipeline.ProcessFrame(frame, SampleRate);

        Assert.True(result.IsVoiced);
        Assert.Equal(69, result.MidiNote); // A4
        Assert.True(Math.Abs(result.CentsDeviation) < 5.0);
    }

    [Fact]
    public void ProcessFrame_WithCustomTargetSampleRate_StillDetectsPitch()
    {
        var pipeline = new PitchAnalysisPipeline { TargetSampleRate = 16000 };

        float[] frame = GenerateSineWave(220.0, SampleRate, 0.4);
        PitchAnalysisResult result = pipeline.ProcessFrame(frame, SampleRate);

        Assert.True(result.IsVoiced);
        double centsError = 1200.0 * Math.Log2(result.RawF0Hz / 220.0);
        Assert.True(Math.Abs(centsError) < 10.0);
    }

    [Fact]
    public void ProcessFrame_OnSilenceAfterVoicedNote_ResetsPitchTrackStages()
    {
        var kalman = new KalmanPitchSmoother(KalmanPitchSmoother.Preset.Smooth);
        var pipeline = new PitchAnalysisPipeline();
        pipeline.PitchTrackStages.Add(kalman);

        float[] voicedFrame = GenerateSineWave(440.0, SampleRate, 0.35);
        for (int i = 0; i < 5; i++)
            pipeline.ProcessFrame(voicedFrame, SampleRate);

        float[] silentFrame = new float[8192];
        PitchAnalysisResult silentResult = pipeline.ProcessFrame(silentFrame, SampleRate);
        Assert.False(silentResult.IsVoiced);

        // After the reset triggered by silence, a new note should converge immediately
        // rather than being dragged toward the previous note's smoothed value.
        float[] newNoteFrame = GenerateSineWave(220.0, SampleRate, 0.35);
        PitchAnalysisResult newNoteResult = pipeline.ProcessFrame(newNoteFrame, SampleRate);

        Assert.True(newNoteResult.IsVoiced);
        Assert.Equal(57, newNoteResult.MidiNote); // A3
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
