using ToneDetector.Core.Framing;
using ToneDetector.Core.Transcript;
using TranscriptModel = ToneDetector.Core.Transcript.Transcript;

namespace ToneDetector.Core.Pipeline;

/// <summary>
/// Runs a fully-decoded mono buffer through the same framing + analysis pipeline used by the
/// real-time path, then segments the resulting frame-by-frame results into a transcript. This
/// is what backs offline/file analysis mode.
/// </summary>
public static class OfflineAnalyzer
{
    public static TranscriptModel Analyze(
        float[] samples,
        int sampleRate,
        PitchAnalysisPipeline pipeline,
        int windowSize,
        int hopSize,
        string sourceInfo,
        double minSilenceMs = 150.0,
        double maxVoicedGapMs = 110.0)
    {
        pipeline.Reset();

        var accumulator = new SlidingWindowFrameAccumulator(windowSize, hopSize);
        var results = new List<PitchAnalysisResult>();
        accumulator.FrameReady += frame => results.Add(pipeline.ProcessFrame(frame, sampleRate));
        accumulator.Write(samples);

        double frameDurationMs = hopSize * 1000.0 / sampleRate;
        return TranscriptBuilder.Build(results, frameDurationMs, sourceInfo, minSilenceMs, maxVoicedGapMs);
    }
}
