using ToneDetector.App.ViewModels;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Tests the delete/move/resize/pitch-change editing methods added to LiveTunerViewModel
/// alongside the existing chord-transform feature (see ChordTransformTests.cs) - same helper setup,
/// same "_committedEvents/Transcript stay in lockstep, matched by reference, replaced via
/// RemoveAt+Insert-at-index" pattern. Unlike chord-transform, these are gated on !IsRecording, so
/// every test here explicitly stops recording after feeding frames, mirroring the real workflow
/// (edit after pressing Stop).</summary>
public class TranscriptEditingTests
{
    private static LiveTunerViewModel CreateViewModel()
    {
        var viewModel = new LiveTunerViewModel(new PipelineSettingsViewModel(), new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()), () => new FakeAudioCapture());
        viewModel.IsRecording = true;
        return viewModel;
    }

    private static void FeedFrames(LiveTunerViewModel viewModel, PitchAnalysisResult frame, int count)
    {
        for (int i = 0; i < count; i++)
            viewModel.SimulateFrameForTest(frame);
    }

    private static PitchAnalysisResult MakeVoicedFrame(double f0Hz) =>
        new(f0Hz, f0Hz, 0, 0, IsVoiced: true, RmsEnergy: 0.5, Aperiodicity: 0.05);

    private static PitchAnalysisResult MakeUnvoicedFrame() =>
        new(0, 0, 0, 0, IsVoiced: false, RmsEnergy: 0.0, Aperiodicity: 1.0);

    private static List<TranscriptEntryViewModel> FeedThreeDistinctNotes(LiveTunerViewModel viewModel)
    {
        FeedFrames(viewModel, MakeVoicedFrame(130.81), 10); // C3 (MIDI 48)
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        FeedFrames(viewModel, MakeVoicedFrame(146.83), 10); // D3 (MIDI 50)
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        FeedFrames(viewModel, MakeVoicedFrame(164.81), 10); // E3 (MIDI 52)
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        viewModel.IsRecording = false;
        return viewModel.Transcript.Where(e => e.Type == "Note").ToList();
    }

    [Fact]
    public void DeleteEntries_RemovesFromCommittedEventsAndTranscript_InLockstep()
    {
        var viewModel = CreateViewModel();
        List<TranscriptEntryViewModel> notes = FeedThreeDistinctNotes(viewModel);
        int countBefore = viewModel.Transcript.Count;

        viewModel.DeleteEntries(new[] { notes[1] }); // the middle note (D3)

        Assert.Equal(countBefore - 1, viewModel.Transcript.Count);
        Assert.Equal(countBefore - 1, viewModel.CommittedEventsForTest.Count);
        Assert.DoesNotContain(viewModel.Transcript, e => e.MidiNumber == 50);
    }

    [Fact]
    public void DeleteEntries_WhileRecording_IsANoOp()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        // IsRecording deliberately left true - bars could be concurrently added/pruned live.

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);
        viewModel.DeleteEntries(new[] { original });

        Assert.Single(viewModel.Transcript); // untouched
    }

    [Fact]
    public void DeleteEntries_RecomputesGapSincePrevForFollowingEntry()
    {
        var viewModel = CreateViewModel();
        // Dead-zone gaps (120ms: > MaxVoicedGapMs, < MinSilenceMs) split into separate Note events
        // with no Silence event in between - this keeps the scenario to plain Note/Note adjacency,
        // so the expected gap is simple arithmetic on committed StartTimeMs/DurationMs rather than
        // depending on LiveTranscriptBuilder's internal Silence-commit/rebase timing.
        FeedFrames(viewModel, MakeVoicedFrame(130.81), 10); // C3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);      // 120ms dead zone
        FeedFrames(viewModel, MakeVoicedFrame(146.83), 10); // D3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);      // 120ms dead zone
        FeedFrames(viewModel, MakeVoicedFrame(164.81), 10); // E3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);     // real silence - flushes E3
        viewModel.IsRecording = false;

        var notes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(3, notes.Count); // confirms no Silence events landed in this scenario at all
        TranscriptEntryViewModel firstNote = notes[0];

        viewModel.DeleteEntries(new[] { notes[1] }); // delete D3, leaving C3 and E3 directly adjacent

        var remaining = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, remaining.Count);
        double expectedGap = remaining[1].StartTimeMs - (firstNote.StartTimeMs + firstNote.DurationMs);
        Assert.Equal(expectedGap, remaining[1].GapSincePrevMs, precision: 3);
    }

    [Fact]
    public void MoveOrResizeEntry_ChangesStartAndDuration()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        viewModel.IsRecording = false;

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);

        TranscriptEntryViewModel? updated = viewModel.MoveOrResizeEntry(original, newStartTimeMs: 500.0, newDurationMs: 300.0);

        Assert.NotNull(updated);
        Assert.Equal(500.0, updated!.StartTimeMs, precision: 3);
        Assert.Equal(300.0, updated.DurationMs, precision: 3);
        Assert.Equal(500.0, viewModel.CommittedEventsForTest[0].StartTimeMs, precision: 3);
        Assert.Equal(300.0, viewModel.CommittedEventsForTest[0].DurationMs, precision: 3);
    }

    [Fact]
    public void MoveOrResizeEntry_GrowingIntoTheNextNeighbor_ShrinksItInsteadOfClamping()
    {
        // PHASE5-PIANOROLL-POLYPHONY-FOLLOWUP.md Part D: growing a Note's duration into a following
        // Note/Silence now cascades - it auto-shrinks the neighbor (down to MinNoteDurationMs) rather
        // than being rejected outright.
        var viewModel = CreateViewModel();
        // A gap strictly between MaxVoicedGapMs (110ms default) and MinSilenceMs (150ms default)
        // splits into two adjacent Note events with no Silence event reported between them (the
        // exact Phase A dead-zone scenario) - the immediate neighbor in _committedEvents really is
        // the next Note directly, making this a realistic cascade scenario.
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);      // 120ms: dead zone, no Silence reported
        FeedFrames(viewModel, MakeVoicedFrame(293.66), 10); // D4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);     // flush the second note via real silence
        viewModel.IsRecording = false;

        var notes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, notes.Count); // confirms no Silence event landed between them
        double secondNoteStartBefore = notes[1].StartTimeMs;
        double secondNoteEndBefore = notes[1].StartTimeMs + notes[1].DurationMs;

        // Try to resize the first note so it massively overruns into the second note's territory.
        TranscriptEntryViewModel? updated = viewModel.MoveOrResizeEntry(notes[0], notes[0].StartTimeMs, newDurationMs: 100000.0);

        Assert.NotNull(updated);
        double updatedEndMs = updated!.StartTimeMs + updated.DurationMs;
        Assert.True(updatedEndMs > secondNoteStartBefore); // grew well past the neighbor's ORIGINAL start

        // The neighbor shrunk (its own end never moved) rather than being displaced or destroyed,
        // stopping exactly at its MinNoteDurationMs floor since the requested growth (100000ms) vastly
        // exceeds the available room.
        TranscriptEntryViewModel neighborAfter = viewModel.Transcript[1];
        Assert.Equal(updatedEndMs, neighborAfter.StartTimeMs, precision: 3);
        Assert.Equal(20.0, neighborAfter.DurationMs, precision: 3); // MinNoteDurationMs
        Assert.Equal(secondNoteEndBefore, neighborAfter.StartTimeMs + neighborAfter.DurationMs, precision: 3);
    }

    [Fact]
    public void MoveOrResizeEntry_GrowingASilenceIntoTheFollowingNote_ShrinksItInsteadOfClamping()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);     // 400ms - real, reported Silence
        FeedFrames(viewModel, MakeVoicedFrame(293.66), 10); // D4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);     // commits D4
        viewModel.IsRecording = false;

        Assert.Equal(3, viewModel.Transcript.Count); // Note, Silence, Note
        TranscriptEntryViewModel silence = viewModel.Transcript[1];
        Assert.Equal("Silence", silence.Type);
        double noteAfterEndBefore = viewModel.Transcript[2].StartTimeMs + viewModel.Transcript[2].DurationMs;

        TranscriptEntryViewModel? updated = viewModel.MoveOrResizeEntry(silence, silence.StartTimeMs, newDurationMs: 100000.0);

        Assert.NotNull(updated);
        double updatedEndMs = updated!.StartTimeMs + updated.DurationMs;
        TranscriptEntryViewModel noteAfter = viewModel.Transcript[2];
        Assert.Equal(updatedEndMs, noteAfter.StartTimeMs, precision: 3);
        Assert.Equal(20.0, noteAfter.DurationMs, precision: 3); // MinNoteDurationMs - Silence/Note share the same floor
        Assert.Equal(noteAfterEndBefore, noteAfter.StartTimeMs + noteAfter.DurationMs, precision: 3);
    }

    [Fact]
    public void MoveOrResizeEntry_ShrinkingStartIntoThePreviousNeighbor_ShrinksItInsteadOfClamping()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);      // 120ms dead zone - no Silence reported
        FeedFrames(viewModel, MakeVoicedFrame(293.66), 10); // D4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);     // flush D4
        viewModel.IsRecording = false;

        var notes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, notes.Count);
        double firstNoteStartBefore = notes[0].StartTimeMs;
        double firstNoteEndBefore = notes[0].StartTimeMs + notes[0].DurationMs; // the old clamp boundary
        TranscriptEntryViewModel secondNote = notes[1];

        // Drag the SECOND note's start far to the left, deep into the first note's territory.
        TranscriptEntryViewModel? updated = viewModel.MoveOrResizeEntry(secondNote, newStartTimeMs: 0.0, secondNote.DurationMs);

        Assert.NotNull(updated);
        Assert.True(updated!.StartTimeMs < firstNoteEndBefore); // grew past where the old clamp would have stopped it

        TranscriptEntryViewModel firstNoteAfter = viewModel.Transcript[0];
        Assert.Equal(firstNoteStartBefore, firstNoteAfter.StartTimeMs, precision: 3); // its own start never moved
        Assert.Equal(20.0, firstNoteAfter.DurationMs, precision: 3); // MinNoteDurationMs
        Assert.Equal(updated.StartTimeMs, firstNoteAfter.StartTimeMs + firstNoteAfter.DurationMs, precision: 3); // shrunk to meet the new start exactly
    }

    [Fact]
    public void MoveOrResizeEntry_WhenNeighborIsAlreadyAtTheFloor_ClampsInsteadOfShrinkingFurther()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);
        FeedFrames(viewModel, MakeVoicedFrame(293.66), 10); // D4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);
        viewModel.IsRecording = false;

        var notes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        // Shrink the neighbor down to the floor first, so it has nothing left to give.
        viewModel.MoveOrResizeEntry(notes[1], notes[1].StartTimeMs, newDurationMs: 20.0);
        TranscriptEntryViewModel neighborAtFloor = viewModel.Transcript[1];

        TranscriptEntryViewModel? updated = viewModel.MoveOrResizeEntry(notes[0], notes[0].StartTimeMs, newDurationMs: 100000.0);

        Assert.NotNull(updated);
        double updatedEndMs = updated!.StartTimeMs + updated.DurationMs;
        Assert.Equal(neighborAtFloor.StartTimeMs, updatedEndMs, precision: 3); // stops exactly at the neighbor's (already-floored) start

        TranscriptEntryViewModel neighborAfter = viewModel.Transcript[1];
        Assert.Equal(neighborAtFloor.StartTimeMs, neighborAfter.StartTimeMs, precision: 3); // untouched - already at the floor
        Assert.Equal(20.0, neighborAfter.DurationMs, precision: 3);
    }

    [Fact]
    public void UndoLastEdit_AfterACascadingResize_RestoresBothTheEntryAndItsShrunkNeighbor()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);
        FeedFrames(viewModel, MakeVoicedFrame(293.66), 10); // D4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);
        viewModel.IsRecording = false;

        var notes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        double firstStartBefore = notes[0].StartTimeMs;
        double firstDurationBefore = notes[0].DurationMs;
        double secondStartBefore = notes[1].StartTimeMs;
        double secondDurationBefore = notes[1].DurationMs;

        viewModel.MoveOrResizeEntry(notes[0], notes[0].StartTimeMs, newDurationMs: 100000.0);
        Assert.True(viewModel.CanUndoEdit);

        viewModel.UndoLastEditCommand.Execute(null);

        Assert.Equal(2, viewModel.Transcript.Count);
        Assert.Equal(firstStartBefore, viewModel.Transcript[0].StartTimeMs, precision: 3);
        Assert.Equal(firstDurationBefore, viewModel.Transcript[0].DurationMs, precision: 3);
        Assert.Equal(secondStartBefore, viewModel.Transcript[1].StartTimeMs, precision: 3);
        Assert.Equal(secondDurationBefore, viewModel.Transcript[1].DurationMs, precision: 3);
        Assert.False(viewModel.CanUndoEdit);
    }

    [Fact]
    public void MoveOrResizeEntry_WhileRecording_IsANoOp()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);
        TranscriptEntryViewModel? updated = viewModel.MoveOrResizeEntry(original, 999.0, 999.0);

        Assert.Null(updated);
        Assert.Equal(original.StartTimeMs, viewModel.Transcript[0].StartTimeMs);
    }

    [Fact]
    public void MoveEntry_ChangesStartAndPitchAtomically_AsOneUndoStep()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4 (MIDI 60)
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        viewModel.IsRecording = false;

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);
        double originalDuration = original.DurationMs;

        TranscriptEntryViewModel? updated = viewModel.MoveEntry(original, newStartTimeMs: 400.0, newMidiNumber: 64);

        Assert.NotNull(updated);
        Assert.Equal(400.0, updated!.StartTimeMs, precision: 3);
        Assert.Equal(64, updated.MidiNumber);
        Assert.Equal(originalDuration, updated.DurationMs, precision: 3); // move preserves duration

        viewModel.UndoLastEditCommand.Execute(null);

        TranscriptEntryViewModel restored = Assert.Single(viewModel.Transcript);
        Assert.Equal(original.StartTimeMs, restored.StartTimeMs, precision: 3);
        Assert.Equal(60, restored.MidiNumber); // both the move AND the pitch change undone together
    }

    [Fact]
    public void ChangeEntryPitch_ResetsCentsDeviationToZero()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        viewModel.IsRecording = false;

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);

        TranscriptEntryViewModel? updated = viewModel.ChangeEntryPitch(original, newMidiNumber: 62);

        Assert.NotNull(updated);
        Assert.Equal(62, updated!.MidiNumber);
        Assert.Equal(0.0, updated.CentsDeviation, precision: 3);
    }

    [Fact]
    public void ChangeEntryPitch_WhileRecording_IsANoOp()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);
        TranscriptEntryViewModel? updated = viewModel.ChangeEntryPitch(original, 62);

        Assert.Null(updated);
    }

    [Fact]
    public void AddEntry_AppendsNewNoteIntoCommittedEventsAndTranscript_InLockstep()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        viewModel.IsRecording = false;
        int countBefore = viewModel.Transcript.Count;

        TranscriptEntryViewModel? added = viewModel.AddEntry(startTimeMs: 5000.0, durationMs: 400.0, midiNumber: 67);

        Assert.NotNull(added);
        Assert.Equal("Note", added!.Type);
        Assert.Equal(67, added.MidiNumber);
        Assert.Equal(5000.0, added.StartTimeMs, precision: 3);
        Assert.Equal(400.0, added.DurationMs, precision: 3);
        Assert.Equal(1.0, added.ConfidenceMean, precision: 3);
        Assert.Equal(0.0, added.CentsDeviation, precision: 3);
        Assert.Equal(countBefore + 1, viewModel.Transcript.Count);
        Assert.Equal(countBefore + 1, viewModel.CommittedEventsForTest.Count);
    }

    [Fact]
    public void AddEntry_InsertsAtCorrectSortedPosition_BetweenTwoExistingNotes()
    {
        var viewModel = CreateViewModel();
        // Dead-zone gap (120ms: > MaxVoicedGapMs, < MinSilenceMs), same rationale as the equivalent
        // Delete/MoveOrResizeEntry tests: keeps this a plain Note/Note adjacency with no Silence
        // event landing in between, so the expected index is simple/predictable.
        FeedFrames(viewModel, MakeVoicedFrame(130.81), 10); // C3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);      // 120ms dead zone
        FeedFrames(viewModel, MakeVoicedFrame(146.83), 10); // D3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);     // flush D3 via real trailing silence
        viewModel.IsRecording = false;

        var notes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, notes.Count); // confirms no Silence event landed between C3 and D3

        double midpointMs = (notes[0].StartTimeMs + notes[0].DurationMs + notes[1].StartTimeMs) / 2.0;
        TranscriptEntryViewModel? added = viewModel.AddEntry(midpointMs, durationMs: 20.0, midiNumber: 55);

        Assert.NotNull(added);
        int index = viewModel.Transcript.IndexOf(added!);
        Assert.Equal(1, index); // lands between the original C3 (index 0) and D3 (now pushed to index 2)
    }

    [Fact]
    public void AddEntry_ClampsToNeighbors_CannotOverlapAdjacentNotes()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(130.81), 10); // C3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);      // 120ms dead zone, no Silence reported
        FeedFrames(viewModel, MakeVoicedFrame(146.83), 10); // D3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);     // flush D3 via real trailing silence
        viewModel.IsRecording = false;

        var notes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, notes.Count);
        double gapStartMs = notes[0].StartTimeMs + notes[0].DurationMs;
        double gapEndMs = notes[1].StartTimeMs;

        // Try to draw a note that massively overruns into the second note's territory.
        TranscriptEntryViewModel? added = viewModel.AddEntry(gapStartMs, durationMs: 100000.0, midiNumber: 60);

        Assert.NotNull(added);
        Assert.True(added!.StartTimeMs + added.DurationMs <= gapEndMs);
    }

    [Fact]
    public void AddEntry_WhileRecording_IsANoOp()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        // IsRecording deliberately left true.
        int countBefore = viewModel.Transcript.Count;

        TranscriptEntryViewModel? added = viewModel.AddEntry(5000.0, 400.0, 67);

        Assert.Null(added);
        Assert.Equal(countBefore, viewModel.Transcript.Count);
    }

    [Fact]
    public void AddEntry_RecomputesGapSincePrevForFollowingEntry()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(130.81), 10); // C3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);      // 120ms dead zone, no Silence reported
        FeedFrames(viewModel, MakeVoicedFrame(146.83), 10); // D3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);     // flush D3 via real trailing silence
        viewModel.IsRecording = false;

        var notesBefore = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, notesBefore.Count);
        TranscriptEntryViewModel firstNote = notesBefore[0];
        TranscriptEntryViewModel secondNote = notesBefore[1];

        // Draw a short note squarely between them, small enough not to trip the clamp.
        double midpointMs = (firstNote.StartTimeMs + firstNote.DurationMs + secondNote.StartTimeMs) / 2.0;
        TranscriptEntryViewModel? added = viewModel.AddEntry(midpointMs, durationMs: 20.0, midiNumber: 60);
        Assert.NotNull(added);

        var notesAfter = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        TranscriptEntryViewModel refreshedSecondNote = notesAfter.Single(e => e.MidiNumber == secondNote.MidiNumber && e.StartTimeMs == secondNote.StartTimeMs);
        double expectedGap = refreshedSecondNote.StartTimeMs - (added!.StartTimeMs + added.DurationMs);
        Assert.Equal(expectedGap, refreshedSecondNote.GapSincePrevMs, precision: 3);
    }

    [Fact]
    public void UndoLastEdit_AfterDelete_RestoresDeletedEntryAtItsOriginalPosition()
    {
        var viewModel = CreateViewModel();
        List<TranscriptEntryViewModel> notes = FeedThreeDistinctNotes(viewModel);

        viewModel.DeleteEntries(new[] { notes[1] });
        Assert.True(viewModel.CanUndoEdit);

        viewModel.UndoLastEditCommand.Execute(null);

        var restoredNotes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(3, restoredNotes.Count);
        Assert.Equal(new[] { 48, 50, 52 }, restoredNotes.Select(e => e.MidiNumber));
        Assert.False(viewModel.CanUndoEdit);
    }

    [Fact]
    public void RecomputeLastEventEndMs_AfterDeletingLastEntry_ReflectsWhicheverEventIsNowLast()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // first note
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        FeedFrames(viewModel, MakeVoicedFrame(293.66), 10); // second (last) note
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        viewModel.IsRecording = false;

        var notesBeforeDelete = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, notesBeforeDelete.Count);

        viewModel.DeleteEntries(new[] { notesBeforeDelete[1] }); // delete the last note

        // LastEventEndMsForTest must reflect whatever now actually sits at the end of
        // _committedEvents (a Silence event, in this scenario - the trailing silence after the
        // second note was still in-progress/uncommitted when recording stopped, so the only
        // remaining events are [first note, the silence between them]) - not a stale value from
        // before the delete.
        TranscriptEvent lastRemaining = viewModel.CommittedEventsForTest[^1];
        Assert.NotNull(viewModel.LastEventEndMsForTest);
        Assert.Equal(lastRemaining.StartTimeMs + lastRemaining.DurationMs, viewModel.LastEventEndMsForTest!.Value, precision: 3);
    }
}
