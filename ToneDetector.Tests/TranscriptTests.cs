using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class TranscriptTests
{
    private const int SampleRate = 48000;
    private const int WindowSize = 4096;
    private const int HopSize = 1024;

    [Fact]
    public void Analyze_OnToneSilenceTone_ProducesNoteSilenceNoteSequence()
    {
        float[] toneA = GenerateSineWave(440.0, SampleRate, 0.5);
        float[] silence = new float[(int)(SampleRate * 0.4)];
        float[] toneB = GenerateSineWave(220.0, SampleRate, 0.5);
        float[] samples = Concat(toneA, silence, toneB);

        var pipeline = new PitchAnalysisPipeline();
        Transcript transcript = OfflineAnalyzer.Analyze(samples, SampleRate, pipeline, WindowSize, HopSize, "test.wav");

        Assert.True(transcript.Events.Count >= 3);
        Assert.Equal(TranscriptEventType.Note, transcript.Events[0].Type);
        Assert.Equal(69, transcript.Events[0].Note!.Value.MidiNumber); // A4

        TranscriptEvent silenceEvent = transcript.Events.First(e => e.Type == TranscriptEventType.Silence);
        Assert.True(silenceEvent.DurationMs > 100);

        TranscriptEvent lastNote = transcript.Events.Last(e => e.Type == TranscriptEventType.Note);
        Assert.Equal(57, lastNote.Note!.Value.MidiNumber); // A3
    }

    [Fact]
    public void Build_BridgesBriefUnvoicedGapInsideSustainedNote()
    {
        // 5 voiced frames, 1 unvoiced "dropout" frame, 5 more voiced frames of the same pitch:
        // should be reported as a single continuous Note, not fragmented by the dropout.
        var frames = new List<PitchAnalysisResult>();
        for (int i = 0; i < 5; i++)
            frames.Add(MakeVoicedFrame(440.0));
        frames.Add(MakeUnvoicedFrame());
        for (int i = 0; i < 5; i++)
            frames.Add(MakeVoicedFrame(440.0));

        double frameDurationMs = 20.0; // short gap of 1 frame = 20ms, well under the default 110ms bridge threshold
        Transcript transcript = TranscriptBuilder.Build(frames, frameDurationMs, "synthetic");

        Assert.Single(transcript.Events);
        Assert.Equal(TranscriptEventType.Note, transcript.Events[0].Type);
        Assert.Equal(220.0, transcript.Events[0].DurationMs, precision: 3);
    }

    [Fact]
    public void Build_100msGapInsideSustainedNote_SplitsAtOldDefault_ButBridgesAtNewDefault()
    {
        // Offline-builder twin of LiveTranscriptBuilderTests' equivalent case - reproduces the
        // real reported bug (a mid-note dropout strictly between the old 60ms bridge tolerance
        // and the 150ms min-silence threshold splitting one note into two fragments with no
        // Silence row shown) and confirms the new 110ms default resolves it, keeping live/offline
        // parity at the new threshold too.
        var frames = new List<PitchAnalysisResult>();
        frames.AddRange(Repeat(MakeVoicedFrame(440.0), 10));
        frames.AddRange(Repeat(MakeUnvoicedFrame(), 5)); // 100ms gap: > 60ms old default, < 150ms min-silence
        frames.AddRange(Repeat(MakeVoicedFrame(440.0), 10));

        Transcript oldDefaultResult = TranscriptBuilder.Build(frames, frameDurationMs: 20.0, sourceInfo: "synthetic",
            minSilenceMs: 150.0, maxVoicedGapMs: 60.0);
        Assert.Equal(2, oldDefaultResult.Events.Count); // split into two Note fragments, no Silence event

        Transcript newDefaultResult = TranscriptBuilder.Build(frames, frameDurationMs: 20.0, sourceInfo: "synthetic");
        Assert.Single(newDefaultResult.Events); // bridged into one continuous Note at the new 110ms default
        Assert.Equal(TranscriptEventType.Note, newDefaultResult.Events[0].Type);
        Assert.Equal(500.0, newDefaultResult.Events[0].DurationMs, precision: 3);
    }

    private static IEnumerable<PitchAnalysisResult> Repeat(PitchAnalysisResult frame, int count)
    {
        for (int i = 0; i < count; i++)
            yield return frame;
    }

    [Fact]
    public void Build_DropsSilenceRunsShorterThanMinimumDuration()
    {
        var frames = new List<PitchAnalysisResult>();
        for (int i = 0; i < 3; i++)
            frames.Add(MakeUnvoicedFrame()); // 3 * 20ms = 60ms, below the default 150ms minimum

        Transcript transcript = TranscriptBuilder.Build(frames, frameDurationMs: 20.0, sourceInfo: "synthetic");

        Assert.Empty(transcript.Events);
    }

    private static PitchAnalysisResult MakeVoicedFrame(double f0Hz) =>
        new(f0Hz, f0Hz, 0, 0, IsVoiced: true, RmsEnergy: 0.5, Aperiodicity: 0.05);

    private static PitchAnalysisResult MakeUnvoicedFrame() =>
        new(0, 0, 0, 0, IsVoiced: false, RmsEnergy: 0.0, Aperiodicity: 1.0);

    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, double durationSeconds)
    {
        int sampleCount = (int)(sampleRate * durationSeconds);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate);
        return samples;
    }

    private static float[] Concat(params float[][] segments) => segments.SelectMany(s => s).ToArray();
}
