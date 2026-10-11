using ToneDetector.Core.Analysis;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Pitch;

namespace ToneDetector.Core.Transcript;

/// <summary>
/// Segments a sequence of per-frame pitch analysis results into Note and Silence events.
/// Bridges brief unvoiced gaps inside a sustained note (e.g. a consonant or a momentary
/// dropout) so they don't fragment one note into several, and drops silence runs shorter
/// than <paramref name="minSilenceMs"/> so normal micro-gaps aren't reported as pauses.
/// </summary>
public static class TranscriptBuilder
{
    public static Transcript Build(
        IReadOnlyList<PitchAnalysisResult> frames,
        double frameDurationMs,
        string sourceInfo,
        double minSilenceMs = 150.0,
        double maxVoicedGapMs = 110.0)
    {
        if (frames.Count == 0)
            return new Transcript(sourceInfo, Array.Empty<TranscriptEvent>());

        bool[] effectiveVoiced = BridgeShortGaps(frames, frameDurationMs, maxVoicedGapMs);

        var events = new List<TranscriptEvent>();
        int groupStart = 0;
        for (int i = 1; i <= frames.Count; i++)
        {
            bool atBoundary = i == frames.Count || effectiveVoiced[i] != effectiveVoiced[groupStart];
            if (!atBoundary)
                continue;

            double startTimeMs = groupStart * frameDurationMs;
            double durationMs = (i - groupStart) * frameDurationMs;

            if (effectiveVoiced[groupStart])
            {
                NoteInfo note = ComputeNoteInfo(frames, groupStart, i, frameDurationMs);
                events.Add(new TranscriptEvent(TranscriptEventType.Note, startTimeMs, durationMs, note));
            }
            else if (durationMs >= minSilenceMs)
            {
                events.Add(new TranscriptEvent(TranscriptEventType.Silence, startTimeMs, durationMs, Note: null));
            }

            groupStart = i;
        }

        return new Transcript(sourceInfo, events);
    }

    private static bool[] BridgeShortGaps(IReadOnlyList<PitchAnalysisResult> frames, double frameDurationMs, double maxVoicedGapMs)
    {
        var voiced = new bool[frames.Count];
        for (int i = 0; i < frames.Count; i++)
            voiced[i] = frames[i].IsVoiced;

        int index = 0;
        while (index < voiced.Length)
        {
            if (voiced[index])
            {
                index++;
                continue;
            }

            int runStart = index;
            while (index < voiced.Length && !voiced[index])
                index++;
            int runEndExclusive = index;

            bool hasVoicedBefore = runStart > 0 && voiced[runStart - 1];
            bool hasVoicedAfter = runEndExclusive < voiced.Length && voiced[runEndExclusive];
            double runDurationMs = (runEndExclusive - runStart) * frameDurationMs;

            if (hasVoicedBefore && hasVoicedAfter && runDurationMs <= maxVoicedGapMs)
            {
                for (int j = runStart; j < runEndExclusive; j++)
                    voiced[j] = true;
            }
        }

        return voiced;
    }

    private static NoteInfo ComputeNoteInfo(IReadOnlyList<PitchAnalysisResult> frames, int start, int endExclusive, double frameDurationMs)
    {
        double centsSum = 0.0;
        double confidenceSum = 0.0;
        int voicedFrameCount = 0;
        var centsDeviationSeries = new List<double>();

        for (int i = start; i < endExclusive; i++)
        {
            PitchAnalysisResult frame = frames[i];
            if (!frame.IsVoiced)
                continue; // a bridged gap frame: the original detector had no pitch here.

            centsSum += NoteQuantizer.FrequencyToAbsoluteCents(frame.SmoothedF0Hz);
            confidenceSum += 1.0 - frame.Aperiodicity;
            voicedFrameCount++;
            centsDeviationSeries.Add(frame.CentsDeviation);
        }

        if (voicedFrameCount == 0)
            return new NoteInfo(0, 0.0, 0.0, 0.0);

        double meanCents = centsSum / voicedFrameCount;
        (int midiNote, double centsDeviation) = NoteQuantizer.QuantizeToNearestNote(meanCents);
        double meanF0Hz = NoteQuantizer.AbsoluteCentsToFrequency(meanCents);
        double meanConfidence = confidenceSum / voicedFrameCount;
        double durationMs = (endExclusive - start) * frameDurationMs;
        VibratoAnalysis? vibrato = VibratoAnalyzer.Analyze(centsDeviationSeries, durationMs);

        return new NoteInfo(midiNote, centsDeviation, meanF0Hz, meanConfidence, vibrato);
    }
}
