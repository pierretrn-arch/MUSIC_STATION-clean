using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class LiveTranscriptBuilderTests
{
    private const double FrameDurationMs = 20.0;

    [Fact]
    public void Feed_OnToneSilenceTone_CommitsNoteSilenceNoteInOrder()
    {
        var builder = new LiveTranscriptBuilder();
        var committed = new List<TranscriptEvent>();

        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 20))
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeUnvoicedFrame(), 20)) // 400ms silence, above default 150ms minimum
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeVoicedFrame(220.0), 20))
            committed.AddRange(builder.Feed(frame, FrameDurationMs));

        TranscriptEvent? trailing = builder.Flush();
        if (trailing is not null)
            committed.Add(trailing);

        Assert.Equal(3, committed.Count);
        Assert.Equal(TranscriptEventType.Note, committed[0].Type);
        Assert.Equal(69, committed[0].Note!.Value.MidiNumber); // A4
        Assert.Equal(TranscriptEventType.Silence, committed[1].Type);
        Assert.Equal(TranscriptEventType.Note, committed[2].Type);
        Assert.Equal(57, committed[2].Note!.Value.MidiNumber); // A3
    }

    [Fact]
    public void Feed_BridgesBriefUnvoicedGapInsideSustainedNote_UntilFlush()
    {
        var builder = new LiveTranscriptBuilder();
        var committed = new List<TranscriptEvent>();

        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 5))
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        committed.AddRange(builder.Feed(MakeUnvoicedFrame(), FrameDurationMs)); // 20ms gap, under 110ms default tolerance
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 5))
            committed.AddRange(builder.Feed(frame, FrameDurationMs));

        Assert.Empty(committed); // nothing commits mid-note

        TranscriptEvent? trailing = builder.Flush();
        Assert.NotNull(trailing);
        Assert.Equal(TranscriptEventType.Note, trailing!.Type);
        Assert.Equal(220.0, trailing.DurationMs, precision: 3); // all 11 frames, including the bridged gap
    }

    [Fact]
    public void Feed_100msGapInsideSustainedNote_SplitsAtOldDefault_ButBridgesAtNewDefault()
    {
        // Reproduces the real reported bug exactly: one sustained note with a mid-note dropout
        // that sits strictly between the old 60ms bridge tolerance and the 150ms min-silence
        // threshold (the observed real-world dropout was 85ms) - the old default silently split
        // one note into two fragments with no Silence row shown between them. Guards the FIX'S
        // REASON (the threshold value), not just an outcome that could pass for unrelated reasons.
        var oldDefaultBuilder = new LiveTranscriptBuilder(minSilenceMs: 150.0, maxVoicedGapMs: 60.0);
        var oldDefaultCommitted = new List<TranscriptEvent>();
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 10))
            oldDefaultCommitted.AddRange(oldDefaultBuilder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeUnvoicedFrame(), 5)) // 100ms gap: > 60ms old default, < 150ms min-silence
            oldDefaultCommitted.AddRange(oldDefaultBuilder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 10))
            oldDefaultCommitted.AddRange(oldDefaultBuilder.Feed(frame, FrameDurationMs));

        Assert.Single(oldDefaultCommitted); // the note fragment committed early when the gap exceeded 60ms
        Assert.Equal(TranscriptEventType.Note, oldDefaultCommitted[0].Type);
        Assert.Equal(200.0, oldDefaultCommitted[0].DurationMs, precision: 3); // only the first 10 frames - split, no Silence event at all

        var newDefaultBuilder = new LiveTranscriptBuilder(); // current defaults: 150ms / 110ms
        var newDefaultCommitted = new List<TranscriptEvent>();
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 10))
            newDefaultCommitted.AddRange(newDefaultBuilder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeUnvoicedFrame(), 5)) // same 100ms gap: now bridged (< 110ms default)
            newDefaultCommitted.AddRange(newDefaultBuilder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 10))
            newDefaultCommitted.AddRange(newDefaultBuilder.Feed(frame, FrameDurationMs));

        Assert.Empty(newDefaultCommitted); // nothing commits mid-note now - the gap bridges cleanly

        TranscriptEvent? trailing = newDefaultBuilder.Flush();
        Assert.NotNull(trailing);
        Assert.Equal(TranscriptEventType.Note, trailing!.Type);
        Assert.Equal(500.0, trailing.DurationMs, precision: 3); // all 25 frames, including the bridged 100ms gap
    }

    [Fact]
    public void MaxVoicedGapMs_ChangedMidGap_AppliesToNextFrame_WithoutDiscardingInProgressSegment()
    {
        // A Settings-tab edit should take effect on the very next frame without losing whatever
        // note is currently accumulating - LiveTunerViewModel relies on this to apply live edits
        // without needing to restart the pipeline.
        var builder = new LiveTranscriptBuilder(minSilenceMs: 150.0, maxVoicedGapMs: 60.0);
        var committed = new List<TranscriptEvent>();

        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 10))
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        committed.AddRange(builder.Feed(MakeUnvoicedFrame(), FrameDurationMs)); // 20ms into what would be a 100ms gap

        builder.MaxVoicedGapMs = 110.0; // raised mid-gap, before the gap has fully elapsed

        foreach (var frame in Repeat(MakeUnvoicedFrame(), 4)) // remaining 80ms of the same 100ms gap
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 10))
            committed.AddRange(builder.Feed(frame, FrameDurationMs));

        Assert.Empty(committed); // the in-progress note wasn't discarded, and the raised threshold now bridges the gap

        TranscriptEvent? trailing = builder.Flush();
        Assert.NotNull(trailing);
        Assert.Equal(TranscriptEventType.Note, trailing!.Type);
        Assert.Equal(500.0, trailing.DurationMs, precision: 3);
    }

    [Fact]
    public void Feed_ProducesIdenticalSequenceToOfflineTranscriptBuilder()
    {
        var frames = new List<PitchAnalysisResult>();
        frames.AddRange(Repeat(MakeVoicedFrame(440.0), 15));
        frames.AddRange(Repeat(MakeUnvoicedFrame(), 2)); // bridged (40ms < 110ms)
        frames.AddRange(Repeat(MakeVoicedFrame(440.0), 10));
        frames.AddRange(Repeat(MakeUnvoicedFrame(), 20)); // real silence (400ms > 150ms)
        frames.AddRange(Repeat(MakeVoicedFrame(330.0), 25));
        frames.AddRange(Repeat(MakeUnvoicedFrame(), 6)); // too short to bridge or report (120ms): between 110-150ms window
        frames.AddRange(Repeat(MakeVoicedFrame(196.0), 12));

        Transcript offlineResult = TranscriptBuilder.Build(frames, FrameDurationMs, "synthetic");

        var liveBuilder = new LiveTranscriptBuilder();
        var liveEvents = new List<TranscriptEvent>();
        foreach (PitchAnalysisResult frame in frames)
            liveEvents.AddRange(liveBuilder.Feed(frame, FrameDurationMs));
        TranscriptEvent? liveTrailing = liveBuilder.Flush();
        if (liveTrailing is not null)
            liveEvents.Add(liveTrailing);

        Assert.Equal(offlineResult.Events.Count, liveEvents.Count);
        for (int i = 0; i < offlineResult.Events.Count; i++)
        {
            TranscriptEvent expected = offlineResult.Events[i];
            TranscriptEvent actual = liveEvents[i];
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.StartTimeMs, actual.StartTimeMs, precision: 3);
            Assert.Equal(expected.DurationMs, actual.DurationMs, precision: 3);
            if (expected.Note is { } expectedNote)
            {
                Assert.NotNull(actual.Note);
                Assert.Equal(expectedNote.MidiNumber, actual.Note!.Value.MidiNumber);
                Assert.Equal(expectedNote.CentsDeviation, actual.Note.Value.CentsDeviation, precision: 6);
            }
        }
    }

    [Fact]
    public void Flush_WithNoFramesFed_ReturnsNull()
    {
        var builder = new LiveTranscriptBuilder();
        Assert.Null(builder.Flush());
    }

    [Fact]
    public void Reset_ClearsInProgressSegment()
    {
        var builder = new LiveTranscriptBuilder();
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 5))
            builder.Feed(frame, FrameDurationMs);

        builder.Reset();

        Assert.Null(builder.Flush());
    }

    [Fact]
    public void Flush_WhileBuildingSilence_DropsTrailingSilenceAndReturnsNull()
    {
        var builder = new LiveTranscriptBuilder();
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 10))
            builder.Feed(frame, FrameDurationMs);
        foreach (var frame in Repeat(MakeUnvoicedFrame(), 20)) // well past the min-silence threshold
            builder.Feed(frame, FrameDurationMs);

        TranscriptEvent? trailing = builder.Flush();

        Assert.Null(trailing);
    }

    [Fact]
    public void Flush_WhileBuildingNote_StillEmitsTrailingNote()
    {
        var builder = new LiveTranscriptBuilder();
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 10))
            builder.Feed(frame, FrameDurationMs);

        TranscriptEvent? trailing = builder.Flush();

        Assert.NotNull(trailing);
        Assert.Equal(TranscriptEventType.Note, trailing!.Type);
    }

    [Fact]
    public void DiscardInProgressSegment_ClearsPendingSegmentButKeepsClockRunning()
    {
        var builder = new LiveTranscriptBuilder();
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 5)) // 100ms of in-progress (uncommitted) note
            builder.Feed(frame, FrameDurationMs);

        builder.DiscardInProgressSegment();

        var committed = new List<TranscriptEvent>();
        foreach (var frame in Repeat(MakeVoicedFrame(220.0), 5))
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        TranscriptEvent? trailing = builder.Flush();
        if (trailing is not null)
            committed.Add(trailing);

        Assert.Single(committed);
        Assert.Equal(57, committed[0].Note!.Value.MidiNumber); // A3 - no contamination from the discarded 440Hz frames
        // Change 8: this note is the first of a new recording session (Discard = "Start" was
        // pressed), so it rebases to StartTimeMs == 0 — not the 100ms raw pipeline-clock value —
        // even though the underlying frame clock itself kept running through the discard
        // (unchanged; that's what keeps relative gap math correct across the rest of the session).
        Assert.Equal(0.0, committed[0].StartTimeMs, precision: 3);
    }

    [Fact]
    public void Feed_TrimsLeadingSilenceBeforeStart_AndRebasesFirstNoteToZero()
    {
        // Mirrors PHASE3-EXPANSION.md Change 8's exact scenario: 5000ms of silence before Start
        // (simulated by DiscardInProgressSegment, exactly what the ViewModel does on Start),
        // 2000ms more silence after Start, then a Note.
        var builder = new LiveTranscriptBuilder();
        var committed = new List<TranscriptEvent>();

        foreach (var frame in Repeat(MakeUnvoicedFrame(), 250)) // 5000ms pre-Start silence
            committed.AddRange(builder.Feed(frame, FrameDurationMs));

        builder.DiscardInProgressSegment(); // Start pressed

        foreach (var frame in Repeat(MakeUnvoicedFrame(), 100)) // 2000ms more silence, post-Start
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 20)) // the first note, ~7000ms on the raw clock
            committed.AddRange(builder.Feed(frame, FrameDurationMs));

        TranscriptEvent? trailing = builder.Flush();
        if (trailing is not null)
            committed.Add(trailing);

        TranscriptEvent firstEvent = Assert.Single(committed);
        Assert.Equal(TranscriptEventType.Note, firstEvent.Type);
        Assert.Equal(0.0, firstEvent.StartTimeMs, precision: 3);
        Assert.Equal(69, firstEvent.Note!.Value.MidiNumber); // A4
    }

    [Fact]
    public void Feed_OnlyTrimsTheLeadingSilence_LaterBetweenNotesSilenceStillReported()
    {
        var builder = new LiveTranscriptBuilder();
        var committed = new List<TranscriptEvent>();

        foreach (var frame in Repeat(MakeUnvoicedFrame(), 100)) // 2000ms leading silence — trimmed
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 20)) // first note
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeUnvoicedFrame(), 20)) // 400ms mid-session silence — NOT trimmed
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeVoicedFrame(220.0), 20)) // second note
            committed.AddRange(builder.Feed(frame, FrameDurationMs));

        TranscriptEvent? trailing = builder.Flush();
        if (trailing is not null)
            committed.Add(trailing);

        Assert.Equal(3, committed.Count); // Note, Silence, Note — no leading Silence at all
        Assert.Equal(TranscriptEventType.Note, committed[0].Type);
        Assert.Equal(0.0, committed[0].StartTimeMs, precision: 3); // rebased: leading silence excluded
        Assert.Equal(TranscriptEventType.Silence, committed[1].Type);
        Assert.Equal(400.0, committed[1].StartTimeMs, precision: 3); // 20 frames * 20ms, right after the first note ends
        Assert.Equal(TranscriptEventType.Note, committed[2].Type);
        Assert.Equal(57, committed[2].Note!.Value.MidiNumber); // A3
    }

    [Fact]
    public void DiscardInProgressSegment_DoesNotReArmLeadingSilenceTrim_ForAResumedSession()
    {
        // A second Start (after Stop, no Clear) resumes the SAME grid/session — silence after
        // resuming is ordinary between-notes silence, not a second "leading" silence to discard,
        // otherwise timestamps across the two Start/Stop cycles would go inconsistent again
        // (the same class of bug PHASE1-FIXES.md Bug 1 already fixed once).
        var builder = new LiveTranscriptBuilder();
        var committed = new List<TranscriptEvent>();

        foreach (var frame in Repeat(MakeVoicedFrame(440.0), 20)) // first note establishes the origin
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeUnvoicedFrame(), 20)) // commits the first note (400ms silence)
            committed.AddRange(builder.Feed(frame, FrameDurationMs));

        builder.DiscardInProgressSegment(); // Stop, then Start again — same session, no Clear

        foreach (var frame in Repeat(MakeUnvoicedFrame(), 20)) // 400ms silence after resuming
            committed.AddRange(builder.Feed(frame, FrameDurationMs));
        foreach (var frame in Repeat(MakeVoicedFrame(220.0), 20))
            committed.AddRange(builder.Feed(frame, FrameDurationMs));

        TranscriptEvent? trailing = builder.Flush();
        if (trailing is not null)
            committed.Add(trailing);

        Assert.Equal(3, committed.Count); // Note, Silence, Note — the post-resume silence IS reported
        Assert.Equal(TranscriptEventType.Silence, committed[1].Type);
    }

    private static IEnumerable<PitchAnalysisResult> Repeat(PitchAnalysisResult frame, int count)
    {
        for (int i = 0; i < count; i++)
            yield return frame;
    }

    private static PitchAnalysisResult MakeVoicedFrame(double f0Hz) =>
        new(f0Hz, f0Hz, 0, 0, IsVoiced: true, RmsEnergy: 0.5, Aperiodicity: 0.05);

    private static PitchAnalysisResult MakeUnvoicedFrame() =>
        new(0, 0, 0, 0, IsVoiced: false, RmsEnergy: 0.0, Aperiodicity: 1.0);

    // ---- Pipeline clock (placing a take on the roll) ----

    [Fact]
    public void ClockMs_CountsTheAudioFed_AndSessionOriginIsTheFirstNotesOnset()
    {
        var builder = new LiveTranscriptBuilder();
        Assert.Equal(0.0, builder.ClockMs);

        for (int i = 0; i < 30; i++)
            builder.Feed(MakeUnvoicedFrame(), 10.0);
        Assert.Equal(300.0, builder.ClockMs, 6);
        Assert.Equal(0.0, builder.SessionOriginMs);

        TranscriptEvent? note = null;
        for (int i = 0; i < 20; i++)
            builder.Feed(MakeVoicedFrame(440.0), 10.0);
        for (int i = 0; i < 30 && note is null; i++)
            note = builder.Feed(MakeUnvoicedFrame(), 10.0).FirstOrDefault(e => e.Type == TranscriptEventType.Note);

        Assert.NotNull(note);
        Assert.Equal(300.0, builder.SessionOriginMs, 6);
        // Raw time of the note = its reported start + the origin: 300 ms into the audio.
        Assert.Equal(300.0, note!.StartTimeMs + builder.SessionOriginMs, 6);
    }
}
