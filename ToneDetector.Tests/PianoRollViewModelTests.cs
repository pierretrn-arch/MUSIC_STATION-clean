using ToneDetector.App.ViewModels;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Tests <see cref="PianoRollViewModel"/> - the Piano Roll's independent, free-form
/// polyphonic note collection (see PHASE5-PIANOROLL-POLYPHONY.md), decoupled from
/// <see cref="LiveTunerViewModel"/>'s transcript. Unlike <c>TranscriptEditingTests.cs</c>, nothing
/// here should ever need a neighbor-clamp regression test - overlap is a first-class feature, not a
/// bug to guard against.</summary>
public class PianoRollViewModelTests
{
    private static LiveTunerViewModel CreateLiveTuner()
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

    private static PlaybackViewModel CreatePlayback() => new(() => new FakeSynth(), () => new FakeWavePlayer(), runClock: false);

    private static PianoRollViewModel CreatePianoRoll(LiveTunerViewModel liveTuner) => new(CreatePlayback(), liveTuner);

    private static PianoRollViewModel CreateStandaloneRoll() => new(CreatePlayback());

    // ---- Live auto-sync (PHASE5-PIANOROLL-POLYPHONY-FOLLOWUP.md, Part A) ----

    [Fact]
    public void LiveMirror_WhileRecording_NewCommittedNotesAppearAutomatically()
    {
        // The doc's explicit acceptance criterion: "Recording a note causes it to appear in the
        // Piano Roll without any button press."
        LiveTunerViewModel liveTuner = CreateLiveTuner(); // IsRecording = true
        var pianoRoll = CreatePianoRoll(liveTuner);
        Assert.Empty(pianoRoll.RollNotes);

        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 10);     // commits the note while still recording

        PianoRollNote mirrored = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal(60, mirrored.MidiNumber);
        Assert.Equal(PianoRollNoteOrigin.SeededFromTranscript, mirrored.Origin);
    }

    [Fact]
    public void StoppedRecording_GridTabEditsAreReflectedInRollNotes()
    {
        // TranscriptEntryViewModel has no stable identity - every Grid-tab edit replaces a row via
        // RemoveAt+Insert, indistinguishable by CollectionChanged shape alone from a brand new note.
        // Confirmed behavior: a Grid-tab edit always fully re-syncs RollNotes from the transcript
        // (there's no cheaper way to guarantee "always reflects" without a stable identity to patch
        // just the one changed row).
        LiveTunerViewModel liveTuner = CreateLiveTuner();
        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 10);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 10);
        liveTuner.IsRecording = false;

        var pianoRoll = CreatePianoRoll(liveTuner);
        Assert.Single(pianoRoll.RollNotes);

        TranscriptEntryViewModel entry = Assert.Single(liveTuner.Transcript);
        liveTuner.MoveOrResizeEntry(entry, newStartTimeMs: 9999, newDurationMs: 50);

        // Recorded ms become beats at the roll's tempo (120 BPM: 500 ms a beat).
        PianoRollNote afterMove = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal(9999.0 / 500.0, afterMove.StartBeat, precision: 6);
        Assert.Equal(50.0 / 500.0, afterMove.DurationBeats, precision: 6);

        liveTuner.ChangeEntryPitch(liveTuner.Transcript[0], 72);
        Assert.Equal(72, Assert.Single(pianoRoll.RollNotes).MidiNumber);

        liveTuner.DeleteEntries(new[] { liveTuner.Transcript[0] });
        Assert.Empty(liveTuner.Transcript);
        Assert.Empty(pianoRoll.RollNotes); // the delete reflects too
    }

    [Fact]
    public void StoppedRecording_GridTabEdit_DiscardsAnyNoteEditedIndependentlyInTheRoll()
    {
        // The disclosed trade-off of "always reflects": a full resync on any Grid-tab edit can't
        // distinguish "a note that came from the transcript" from "a note the user drew/moved/
        // resized/chord-transformed directly in the roll" - it wipes both back to whatever the
        // transcript currently says.
        LiveTunerViewModel liveTuner = CreateLiveTuner();
        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 10);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 10);
        liveTuner.IsRecording = false;

        var pianoRoll = CreatePianoRoll(liveTuner);
        PianoRollNote drawn = pianoRoll.AddNote(5000, 500, 67); // roll-only, no transcript counterpart
        Assert.Equal(2, pianoRoll.RollNotes.Count);

        TranscriptEntryViewModel entry = Assert.Single(liveTuner.Transcript);
        liveTuner.ChangeEntryPitch(entry, 72); // any Grid-tab edit, unrelated to the drawn note

        Assert.DoesNotContain(pianoRoll.RollNotes, n => n.Id == drawn.Id); // discarded by the resync
        Assert.Single(pianoRoll.RollNotes);
    }

    [Fact]
    public void ClearTranscript_EmptiesRollNotesAndUndoStack()
    {
        LiveTunerViewModel liveTuner = CreateLiveTuner();
        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 10);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 10);
        liveTuner.IsRecording = false;

        var pianoRoll = CreatePianoRoll(liveTuner);
        Assert.Single(pianoRoll.RollNotes);
        pianoRoll.AddNote(2000, 500, 60); // a roll-only edit, so there's something for undo to clear
        Assert.True(pianoRoll.CanUndoEdit);

        liveTuner.ClearCommand.Execute(null);

        Assert.Empty(pianoRoll.RollNotes);
        Assert.False(pianoRoll.CanUndoEdit);
    }

    [Fact]
    public void InitialSync_AtConstruction_CopiesWhateverTheTranscriptAlreadyHas_IncludingChordGroupId()
    {
        LiveTunerViewModel liveTuner = CreateLiveTuner();
        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 10);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 10);
        liveTuner.IsRecording = false;

        TranscriptEntryViewModel original = Assert.Single(liveTuner.Transcript);
        liveTuner.ApplyChordTransform(new[] { original }, ChordQuality.Minor); // transcript now has 3 chord-grouped rows

        var pianoRoll = CreatePianoRoll(liveTuner); // constructor's initial resync picks up current transcript state

        Assert.Equal(3, pianoRoll.RollNotes.Count);
        Guid? groupId = pianoRoll.RollNotes[0].ChordGroupId;
        Assert.NotNull(groupId);
        Assert.All(pianoRoll.RollNotes, n => Assert.Equal(groupId, n.ChordGroupId));
    }

    // ---- Move / Resize / Delete / Add - unclamped ----

    [Fact]
    public void MoveNote_UnclampedOverlap_AllowedAtAnyPitchOrTime()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote a = pianoRoll.AddNote(0, 500, 60);
        PianoRollNote b = pianoRoll.AddNote(1000, 500, 64);

        // Move b directly on top of a - same pitch AND same time as a. Must not be clamped/blocked.
        PianoRollNote? moved = pianoRoll.MoveNote(b.Id, 0, 60);

        Assert.NotNull(moved);
        Assert.Equal(0.0, moved!.StartBeat, precision: 3);
        Assert.Equal(60, moved.MidiNumber);
        Assert.Equal(2, pianoRoll.RollNotes.Count); // both notes still present, fully overlapping
    }

    [Fact]
    public void MoveNote_FloorsStartAtZero()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote note = pianoRoll.AddNote(1000, 500, 60);

        PianoRollNote? moved = pianoRoll.MoveNote(note.Id, -5000, 60);

        Assert.NotNull(moved);
        Assert.Equal(0.0, moved!.StartBeat, precision: 3);
    }

    [Fact]
    public void ResizeNote_RightEdge_ChangesDurationOnly()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote note = pianoRoll.AddNote(1000, 500, 60);

        PianoRollNote? resized = pianoRoll.ResizeNote(note.Id, newStartBeat: 1000, newDurationBeats: 5000);

        Assert.NotNull(resized);
        Assert.Equal(1000.0, resized!.StartBeat, precision: 3);
        Assert.Equal(5000.0, resized.DurationBeats, precision: 3);
    }

    [Fact]
    public void ResizeNote_LeftEdge_ChangesStartAndDuration()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote note = pianoRoll.AddNote(1000, 500, 60); // end = 1500

        PianoRollNote? resized = pianoRoll.ResizeNote(note.Id, newStartBeat: 1200, newDurationBeats: 300); // end stays 1500

        Assert.NotNull(resized);
        Assert.Equal(1200.0, resized!.StartBeat, precision: 3);
        Assert.Equal(300.0, resized.DurationBeats, precision: 3);
    }

    [Fact]
    public void ResizeNote_FloorsAtMinDuration()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote note = pianoRoll.AddNote(0, 500, 60);

        PianoRollNote? resized = pianoRoll.ResizeNote(note.Id, newStartBeat: 0, newDurationBeats: -100);

        Assert.NotNull(resized);
        Assert.True(resized!.DurationBeats > 0);
    }

    [Fact]
    public void DeleteNotes_RemovesGivenIds()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote a = pianoRoll.AddNote(0, 500, 60);
        PianoRollNote b = pianoRoll.AddNote(1000, 500, 64);
        pianoRoll.AddNote(2000, 500, 67);

        pianoRoll.DeleteNotes(new[] { a.Id, b.Id });

        PianoRollNote remaining = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal(67, remaining.MidiNumber);
    }

    [Fact]
    public void AddNote_UnclampedLength_MatchesExactRequestedLength()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        pianoRoll.AddNote(0, 500, 60); // pre-existing note, to prove drawing across it isn't clamped

        PianoRollNote added = pianoRoll.AddNote(startBeat: 100, durationBeats: 1000, midiNumber: 62);

        Assert.Equal(1000.0, added.DurationBeats, precision: 3); // NOT clamped down to fit before/around the existing note
        Assert.Equal(PianoRollNoteOrigin.Drawn, added.Origin);
    }

    [Fact]
    public void AddNote_OverlappingExistingNote_IsAllowed()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        pianoRoll.AddNote(0, 1000, 60);

        PianoRollNote added = pianoRoll.AddNote(startBeat: 200, durationBeats: 500, midiNumber: 64); // different pitch, fully inside the first note's span

        Assert.Equal(2, pianoRoll.RollNotes.Count);
        Assert.Contains(pianoRoll.RollNotes, n => n.Id == added.Id);
    }

    [Fact]
    public void AddNote_SamePitchOverlap_BothNotesIndependentlyPresent()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote first = pianoRoll.AddNote(0, 1000, 60);

        // Same pitch, overlapping time range - a deliberate retrigger, not a rejected duplicate.
        PianoRollNote second = pianoRoll.AddNote(startBeat: 200, durationBeats: 500, midiNumber: 60);

        Assert.Equal(2, pianoRoll.RollNotes.Count);
        Assert.Contains(pianoRoll.RollNotes, n => n.Id == first.Id);
        Assert.Contains(pianoRoll.RollNotes, n => n.Id == second.Id);
    }

    // ---- Undo (generic command stack) ----

    [Fact]
    public void Undo_AfterMove_RestoresOriginalPosition()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote note = pianoRoll.AddNote(0, 500, 60);

        pianoRoll.MoveNote(note.Id, 2000, 67);
        Assert.True(pianoRoll.CanUndoEdit);

        pianoRoll.UndoCommand.Execute(null);

        PianoRollNote restored = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal(0.0, restored.StartBeat, precision: 3);
        Assert.Equal(60, restored.MidiNumber);
        Assert.True(pianoRoll.CanUndoEdit); // multi-level: the original AddNote is still on the stack
    }

    [Fact]
    public void Undo_AfterResize_RestoresOriginalDuration()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote note = pianoRoll.AddNote(0, 500, 60);

        pianoRoll.ResizeNote(note.Id, 0, 5000);
        pianoRoll.UndoCommand.Execute(null);

        PianoRollNote restored = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal(500.0, restored.DurationBeats, precision: 3);
    }

    [Fact]
    public void Undo_AfterDelete_RestoresDeletedNotes()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote a = pianoRoll.AddNote(0, 500, 60);
        PianoRollNote b = pianoRoll.AddNote(1000, 500, 64);

        pianoRoll.DeleteNotes(new[] { a.Id, b.Id });
        Assert.Empty(pianoRoll.RollNotes);

        pianoRoll.UndoCommand.Execute(null);

        Assert.Equal(2, pianoRoll.RollNotes.Count);
        Assert.Contains(pianoRoll.RollNotes, n => n.Id == a.Id);
        Assert.Contains(pianoRoll.RollNotes, n => n.Id == b.Id);
    }

    [Fact]
    public void Undo_AfterDraw_RemovesTheDrawnNote()
    {
        // The doc's explicit acceptance criterion: "Undo reverses a draw, not just Move/Resize/
        // Delete" - structurally impossible under the transcript's old per-shape undo, trivial here.
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        pianoRoll.AddNote(0, 500, 60);
        Assert.True(pianoRoll.CanUndoEdit);

        pianoRoll.UndoCommand.Execute(null);

        Assert.Empty(pianoRoll.RollNotes);
        Assert.False(pianoRoll.CanUndoEdit);
    }

    [Fact]
    public void Undo_MultiLevel_WalksBackThroughMultipleEditsInOrder()
    {
        // PHASE5-PIANOROLL-POLYPHONY.md's "Follow-up - Multi-level undo": N sequential edits
        // followed by N undos must return RollNotes to its exact pre-edit state, not just reverse
        // the single most recent one.
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote a = pianoRoll.AddNote(0, 500, 60);
        PianoRollNote b = pianoRoll.AddNote(1000, 500, 64);
        Assert.True(pianoRoll.CanUndoEdit);

        // First undo reverses only the most recent edit (adding b) - a's own add is still on the stack.
        pianoRoll.UndoCommand.Execute(null);
        PianoRollNote remaining = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal(a.Id, remaining.Id);
        Assert.True(pianoRoll.CanUndoEdit); // one more step (adding a) is still undoable

        // Second undo walks back further, reversing the first edit too.
        pianoRoll.UndoCommand.Execute(null);
        Assert.Empty(pianoRoll.RollNotes);
        Assert.False(pianoRoll.CanUndoEdit);
    }

    [Fact]
    public void ClearTranscript_ClearsTheEntireUndoStack_AndUndoAfterwardIsASafeNoOp()
    {
        LiveTunerViewModel liveTuner = CreateLiveTuner();
        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 10);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 10);
        liveTuner.IsRecording = false;

        var pianoRoll = CreatePianoRoll(liveTuner);
        pianoRoll.AddNote(2000, 500, 60);
        pianoRoll.AddNote(3000, 500, 64); // two edits on the stack, not just one
        Assert.True(pianoRoll.CanUndoEdit);

        liveTuner.ClearCommand.Execute(null); // destructive full reset
        Assert.False(pianoRoll.CanUndoEdit);
        Assert.Empty(pianoRoll.RollNotes);

        // Attempting undo right after a Clear is a safe no-op, not a crash or a stale-note edit.
        pianoRoll.UndoCommand.Execute(null);
        Assert.Empty(pianoRoll.RollNotes);
        Assert.False(pianoRoll.CanUndoEdit);
    }

    // ---- Chord transform ----

    [Fact]
    public void ApplyChordTransform_ProducesIndependentNotes_SharingChordGroupId()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote root = pianoRoll.AddNote(0, 500, 60);

        IReadOnlyList<PianoRollNote> voices = pianoRoll.ApplyChordTransform(new[] { root.Id }, ChordQuality.Minor);

        Assert.Equal(3, voices.Count);
        Assert.Equal(3, pianoRoll.RollNotes.Count);
        Assert.Equal(new[] { 60, 63, 67 }, pianoRoll.RollNotes.Select(n => n.MidiNumber));
        Guid? groupId = pianoRoll.RollNotes[0].ChordGroupId;
        Assert.NotNull(groupId);
        Assert.All(pianoRoll.RollNotes, n => Assert.Equal(groupId, n.ChordGroupId));
    }

    [Fact]
    public void ApplyChordTransform_VoicesCanFreelyOverlap_WithAPreExistingNoteAtTheSamePitchAndTime()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        pianoRoll.AddNote(0, 500, 63); // pre-existing note that a chord voice will land exactly on top of
        PianoRollNote root = pianoRoll.AddNote(0, 500, 60);

        IReadOnlyList<PianoRollNote> voices = pianoRoll.ApplyChordTransform(new[] { root.Id }, ChordQuality.Minor);

        // Minor = {0, 3, 7} semitones -> voice at 63 exactly overlaps the pre-existing note (same
        // pitch, same time) - must not be blocked/clamped in any way.
        Assert.Equal(3, voices.Count);
        Assert.Equal(4, pianoRoll.RollNotes.Count); // 1 pre-existing + 3 chord voices
        Assert.Equal(2, pianoRoll.RollNotes.Count(n => n.MidiNumber == 63));
    }

    [Fact]
    public void UndoChordTransform_RestoresOriginalNoteAtCorrectPosition()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote root = pianoRoll.AddNote(1000, 500, 60);

        pianoRoll.ApplyChordTransform(new[] { root.Id }, ChordQuality.Diminished);
        Assert.Equal(3, pianoRoll.RollNotes.Count);

        pianoRoll.UndoCommand.Execute(null);

        PianoRollNote restored = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal(60, restored.MidiNumber);
        Assert.Equal(1000.0, restored.StartBeat, precision: 3);
        Assert.Equal(500.0, restored.DurationBeats, precision: 3);
        Assert.Null(restored.ChordGroupId);
    }

    [Fact]
    public void UndoChordTransform_AfterMultiNoteTransform_RestoresBothOriginalNotesAtCorrectPositions()
    {
        var pianoRoll = CreatePianoRoll(CreateLiveTuner());
        PianoRollNote first = pianoRoll.AddNote(0, 500, 48);
        PianoRollNote second = pianoRoll.AddNote(1000, 500, 50);

        pianoRoll.ApplyChordTransform(new[] { first.Id, second.Id }, ChordQuality.Dominant7th); // 4 voices each

        pianoRoll.UndoCommand.Execute(null);

        Assert.Equal(2, pianoRoll.RollNotes.Count);
        Assert.Contains(pianoRoll.RollNotes, n => n.MidiNumber == 48 && n.StartBeat == 0);
        Assert.Contains(pianoRoll.RollNotes, n => n.MidiNumber == 50 && n.StartBeat == 1000);
        Assert.All(pianoRoll.RollNotes, n => Assert.Null(n.ChordGroupId));
    }
    // ---- Redo ----

    [Fact]
    public void Redo_ReappliesTheUndoneEdit()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote note = pianoRoll.AddNote(0, 1, 60);
        pianoRoll.MoveNote(note.Id, 4, 62);

        pianoRoll.UndoCommand.Execute(null);
        Assert.Equal(0.0, Assert.Single(pianoRoll.RollNotes).StartBeat);
        Assert.True(pianoRoll.CanRedoEdit);

        pianoRoll.RedoCommand.Execute(null);
        PianoRollNote redone = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal((4.0, 62), (redone.StartBeat, redone.MidiNumber));
        Assert.Equal(note.Id, redone.Id);
        Assert.False(pianoRoll.CanRedoEdit);
    }

    [Fact]
    public void Redo_AfterUndoingSeveralSteps_ReplaysThemInOrder_WithTheSameNotes()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote root = pianoRoll.AddNote(0, 1, 60);
        IReadOnlyList<PianoRollNote> voices = pianoRoll.ApplyChordTransform(new[] { root.Id }, ChordQuality.Major);
        pianoRoll.MoveNote(voices[1].Id, 2, 64); // refers to a voice id - redo must bring back the same voice

        pianoRoll.UndoCommand.Execute(null);
        pianoRoll.UndoCommand.Execute(null);
        Assert.Equal(root.Id, Assert.Single(pianoRoll.RollNotes).Id);

        pianoRoll.RedoCommand.Execute(null);
        pianoRoll.RedoCommand.Execute(null);

        Assert.Equal(voices.Select(v => v.Id).OrderBy(i => i), pianoRoll.RollNotes.Select(n => n.Id).OrderBy(i => i));
        Assert.Equal(2.0, pianoRoll.RollNotes.Single(n => n.Id == voices[1].Id).StartBeat);
    }

    [Fact]
    public void NewEdit_ClearsRedo()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.AddNote(0, 1, 60);
        pianoRoll.UndoCommand.Execute(null);
        Assert.True(pianoRoll.CanRedoEdit);

        pianoRoll.AddNote(1, 1, 62);

        Assert.False(pianoRoll.CanRedoEdit);
    }

    [Fact]
    public void UndoAndEditing_AreOffWhileRecording()
    {
        LiveTunerViewModel liveTuner = CreateLiveTuner();
        liveTuner.IsRecording = false;
        var pianoRoll = CreatePianoRoll(liveTuner);
        pianoRoll.AddNote(0, 1, 60);

        liveTuner.IsRecording = true;

        Assert.False(pianoRoll.UndoCommand.CanExecute(null));
        Assert.False(pianoRoll.CanEdit);
    }

    // ---- Move several, Split ----

    [Fact]
    public void MoveNotes_MovesTheWholeSelectionAsOneStep()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote a = pianoRoll.AddNote(0, 1, 60);
        PianoRollNote b = pianoRoll.AddNote(1, 1, 64);

        pianoRoll.MoveNotes(new[] { a.Id, b.Id }, 2, -1);

        Assert.Equal(new[] { (2.0, 59), (3.0, 63) }, pianoRoll.RollNotes.Select(n => (n.StartBeat, n.MidiNumber)));
        pianoRoll.UndoCommand.Execute(null);
        Assert.Equal(new[] { (0.0, 60), (1.0, 64) }, pianoRoll.RollNotes.Select(n => (n.StartBeat, n.MidiNumber)));
    }

    [Fact]
    public void SplitNote_ReplacesTheNoteWithTwoHalves_AndUndoJoinsThemBack()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote note = pianoRoll.AddNote(1, 2, 60);

        var halves = pianoRoll.SplitNote(note.Id, 2.5);

        Assert.NotNull(halves);
        Assert.Equal(new[] { (1.0, 1.5), (2.5, 0.5) }, pianoRoll.RollNotes.OrderBy(n => n.StartBeat).Select(n => (n.StartBeat, n.DurationBeats)));
        pianoRoll.UndoCommand.Execute(null);
        PianoRollNote whole = Assert.Single(pianoRoll.RollNotes);
        Assert.Equal((note.Id, 2.0), (whole.Id, whole.DurationBeats));
    }

    [Fact]
    public void SplitNote_OutsideTheNote_DoesNothing()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote note = pianoRoll.AddNote(1, 2, 60);

        Assert.Null(pianoRoll.SplitNote(note.Id, 5));
        Assert.Single(pianoRoll.RollNotes);
    }

    // ---- Clipboard ----

    [Fact]
    public void CopyThenPaste_PutsTheEarliestNoteAtThePlayhead_KeepingSpacing()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote a = pianoRoll.AddNote(1, 1, 60);
        PianoRollNote b = pianoRoll.AddNote(2, 0.5, 67);
        pianoRoll.Copy(new[] { a.Id, b.Id });
        pianoRoll.PositionBeats = 8;

        IReadOnlyList<PianoRollNote> pasted = pianoRoll.Paste();

        Assert.Equal(new[] { (8.0, 60), (9.0, 67) }, pasted.Select(n => (n.StartBeat, n.MidiNumber)));
        Assert.Equal(4, pianoRoll.RollNotes.Count);
        pianoRoll.UndoCommand.Execute(null);
        Assert.Equal(2, pianoRoll.RollNotes.Count);
    }

    [Fact]
    public void Cut_RemovesTheNotes_AndPasteBringsThemBackElsewhere()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote a = pianoRoll.AddNote(1, 1, 60);

        pianoRoll.Cut(new[] { a.Id });
        Assert.Empty(pianoRoll.RollNotes);
        Assert.True(pianoRoll.CanPaste);

        pianoRoll.PositionBeats = 3;
        PianoRollNote pasted = Assert.Single(pianoRoll.Paste());
        Assert.Equal(3.0, pasted.StartBeat);
        Assert.NotEqual(a.Id, pasted.Id);
    }

    [Fact]
    public void Paste_IsUnavailableDuringPlayback()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote a = pianoRoll.AddNote(0, 1, 60);
        pianoRoll.Copy(new[] { a.Id });

        pianoRoll.IsPlaying = true;

        Assert.False(pianoRoll.CanPaste);
        Assert.Empty(pianoRoll.Paste());
        Assert.Single(pianoRoll.RollNotes);
    }

    [Fact]
    public void Clipboard_IsSharedBetweenTabs()
    {
        var liveRoll = CreateStandaloneRoll();
        var fileRoll = CreateStandaloneRoll();
        PianoRollNote a = liveRoll.AddNote(0, 1, 72);

        liveRoll.Copy(new[] { a.Id });

        Assert.Equal(72, Assert.Single(fileRoll.Paste()).MidiNumber);
    }

    // ---- Tempo, playhead, Stop, loop ----

    [Fact]
    public void Tempo_IsClampedToWholeBpm_AndNotesKeepTheirBeats()
    {
        var pianoRoll = CreateStandaloneRoll();
        PianoRollNote note = pianoRoll.AddNote(4, 1, 60);

        pianoRoll.Bpm = 300;
        Assert.Equal(240.0, pianoRoll.Bpm);
        pianoRoll.Bpm = 90.4;
        Assert.Equal(90.0, pianoRoll.Bpm);

        Assert.Equal(note, Assert.Single(pianoRoll.RollNotes));
    }

    [Fact]
    public void TempoSteppers_StopAtTheRangeEnds()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.Bpm = 240;
        Assert.False(pianoRoll.IncreaseTempoCommand.CanExecute(null));
        pianoRoll.DecreaseTempoCommand.Execute(null);
        Assert.Equal(239.0, pianoRoll.Bpm);
    }

    [Fact]
    public void LiveMirror_ConvertsRecordedMsToBeatsAtTheTempo()
    {
        LiveTunerViewModel liveTuner = CreateLiveTuner();
        var pianoRoll = CreatePianoRoll(liveTuner);
        pianoRoll.Bpm = 60; // 1000 ms a beat

        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 10);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 10);

        PianoRollNote note = Assert.Single(pianoRoll.RollNotes);
        TranscriptEntryViewModel entry = liveTuner.Transcript.First(e => e.Type == "Note");
        Assert.Equal(entry.DurationMs / 1000.0, note.DurationBeats, precision: 6);
    }

    [Fact]
    public void Stop_GoesToTheLoopStartFirst_ThenToZero()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.AddNote(0, 16, 60);
        pianoRoll.SetLoop(4, 8);
        pianoRoll.IsLoopEnabled = true;
        pianoRoll.PositionBeats = 10;

        pianoRoll.StopCommand.Execute(null);
        Assert.Equal(4.0, pianoRoll.PositionBeats);
        pianoRoll.StopCommand.Execute(null);
        Assert.Equal(0.0, pianoRoll.PositionBeats);
    }

    [Fact]
    public void Stop_WithLoopOff_GoesToZero()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.SetLoop(4, 8);
        pianoRoll.PositionBeats = 10;

        pianoRoll.StopCommand.Execute(null);

        Assert.Equal(0.0, pianoRoll.PositionBeats);
    }

    [Fact]
    public void Loop_FollowsTheNotesUntilAHandleIsDragged()
    {
        var pianoRoll = CreateStandaloneRoll();
        Assert.Equal((0.0, 4.0), (pianoRoll.LoopStartBeat, pianoRoll.LoopEndBeat));

        pianoRoll.AddNote(5, 1, 60);
        Assert.Equal(8.0, pianoRoll.LoopEndBeat);

        pianoRoll.SetLoop(2, 3);
        pianoRoll.AddNote(20, 1, 60);
        Assert.Equal((2.0, 3.0), (pianoRoll.LoopStartBeat, pianoRoll.LoopEndBeat));
    }

    [Fact]
    public void SetLoop_KeepsAtLeastOneBeat_AndOrdersTheHandles()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.SetLoop(6, 6);
        Assert.Equal((6.0, 7.0), (pianoRoll.LoopStartBeat, pianoRoll.LoopEndBeat));
        pianoRoll.SetLoop(8, 2);
        Assert.Equal((2.0, 8.0), (pianoRoll.LoopStartBeat, pianoRoll.LoopEndBeat));
    }

    [Fact]
    public void PositionText_IsBarBeatStep_AndEndTextIsTheLastNoteEnd()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.AddNote(16, 2.5, 60);
        pianoRoll.PositionBeats = 9;

        Assert.Equal("3.2.1", pianoRoll.PositionText);
        Assert.Equal("5.3.3", pianoRoll.EndText);
    }

    [Fact]
    public void EndText_NeverReadsLessThanThePosition()
    {
        var pianoRoll = CreateStandaloneRoll(); // an empty take
        var raised = new List<string?>();
        pianoRoll.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        pianoRoll.PositionBeats = 3.75;

        Assert.Equal(pianoRoll.PositionText, pianoRoll.EndText);
        Assert.Contains(nameof(PianoRollViewModel.EndText), raised);
    }

    [Fact]
    public void ReplaceAll_LoadsNotes_ClearsHistory_AndResetsLoopAndPlayhead()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.AddNote(0, 1, 60);
        pianoRoll.SetLoop(1, 2);
        pianoRoll.PositionBeats = 5;

        pianoRoll.ReplaceAll(new[] { new PianoRollNote(Guid.NewGuid(), 64, 0, 9) });

        Assert.Equal(64, Assert.Single(pianoRoll.RollNotes).MidiNumber);
        Assert.False(pianoRoll.CanUndoEdit);
        Assert.Equal((0.0, 12.0), (pianoRoll.LoopStartBeat, pianoRoll.LoopEndBeat));
        Assert.Equal(0.0, pianoRoll.PositionBeats);
    }

    [Fact]
    public void ToolFlags_FollowTheActiveTool()
    {
        var pianoRoll = CreateStandaloneRoll();
        Assert.True(pianoRoll.IsSelectTool);

        pianoRoll.IsSplitTool = true;

        Assert.Equal(EditTool.Split, pianoRoll.Tool);
        Assert.False(pianoRoll.IsSelectTool);
        pianoRoll.IsDrawTool = false; // unchecking a radio never changes the tool by itself
        Assert.Equal(EditTool.Split, pianoRoll.Tool);
    }

    [Fact]
    public void GridOptions_ListEveryDivisionInMenuOrder()
    {
        Assert.Equal(GridDivisions.All, PianoRollViewModel.GridOptions.Select(o => o.Division));
        Assert.Equal("1/4 (beat)", PianoRollViewModel.GridOptions[2].Label);
        Assert.Equal(GridDivision.Sixteenth, CreateStandaloneRoll().Grid);
    }

    // ---- Recording into the roll (Record, count-in, take position) ----

    private static (LiveTunerViewModel LiveTuner, PianoRollViewModel Roll, PlaybackViewModel Engine) CreateRecordingSetup()
    {
        PlaybackViewModel engine = CreatePlayback();
        var liveTuner = new LiveTunerViewModel(new PipelineSettingsViewModel(), engine, () => new FakeAudioCapture());
        liveTuner.IsPipelineRunning = true;
        liveTuner.FrameDurationMsForTest = 10;
        var roll = new PianoRollViewModel(engine, liveTuner);
        liveTuner.PianoRoll = roll;
        return (liveTuner, roll, engine);
    }

    [Fact]
    public void Record_WithTheMetronome_CountsInABar_ThenRecordsAtThePlayhead() => StaTestRunner.Run(() =>
    {
        var (liveTuner, roll, engine) = CreateRecordingSetup();
        roll.IsMetronomeOn = true;
        roll.PositionBeats = 8;

        roll.RecordCommand.Execute(null);
        Assert.True(roll.IsCountingIn);
        Assert.False(liveTuner.IsRecording);
        Assert.False(roll.PlayPauseCommand.CanExecute(null));

        engine.AdvanceForTest(4);
        Assert.False(roll.IsCountingIn);
        Assert.True(liveTuner.IsRecording);
        Assert.Equal(8.0, roll.LastTakeStartBeat, 6);

        // 10 ms frames: 200 ms of voice, then 300 ms of silence (past the 110 ms gap that ends a note).
        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 20);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 30);

        PianoRollNote note = Assert.Single(roll.RollNotes);
        Assert.Equal(8.0, note.StartBeat, 6); // sung straight away: on the take's first beat
    });

    [Fact]
    public void Record_KeepsTheSilenceBeforeTheFirstNote() => StaTestRunner.Run(() =>
    {
        var (liveTuner, roll, _) = CreateRecordingSetup();
        roll.PositionBeats = 4;
        roll.RecordCommand.Execute(null); // metronome off: records straight away
        Assert.True(liveTuner.IsRecording);

        FeedFrames(liveTuner, MakeUnvoicedFrame(), 30);
        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 20);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 30);

        // 30 silent 10 ms frames = 300 ms = 0.6 beat at 120 BPM.
        PianoRollNote note = Assert.Single(roll.RollNotes);
        Assert.Equal(4.6, note.StartBeat, 6);
    });

    [Fact]
    public void StopRecording_EndsTheTake_AndTheClock() => StaTestRunner.Run(() =>
    {
        var (liveTuner, roll, engine) = CreateRecordingSetup();
        roll.RecordCommand.Execute(null);
        Assert.True(roll.StopRecordingCommand.CanExecute(null));

        roll.StopRecordingCommand.Execute(null);

        Assert.False(liveTuner.IsRecording);
        Assert.False(engine.IsRecordingClock);
        Assert.True(roll.RecordCommand.CanExecute(null));
    });

    [Fact]
    public void StopRecording_DuringTheCountIn_Cancels() => StaTestRunner.Run(() =>
    {
        var (liveTuner, roll, engine) = CreateRecordingSetup();
        roll.IsMetronomeOn = true;
        roll.RecordCommand.Execute(null);

        roll.StopRecordingCommand.Execute(null);
        engine.AdvanceForTest(8);

        Assert.False(roll.IsCountingIn);
        Assert.False(liveTuner.IsRecording);
    });

    [Fact]
    public void Record_NeedsTheTunerListening() => StaTestRunner.Run(() =>
    {
        var (liveTuner, roll, _) = CreateRecordingSetup();
        liveTuner.IsPipelineRunning = false;
        Assert.False(roll.RecordCommand.CanExecute(null));
        Assert.False(CreateStandaloneRoll().RecordCommand.CanExecute(null)); // File Analysis has no Record
    });

    // ---- Detection data (confidence, cents, chord) ----

    [Fact]
    public void LiveMirror_CarriesConfidenceAndCents()
    {
        LiveTunerViewModel liveTuner = CreateLiveTuner();
        var pianoRoll = CreatePianoRoll(liveTuner);

        FeedFrames(liveTuner, MakeVoicedFrame(261.63), 10);
        FeedFrames(liveTuner, MakeUnvoicedFrame(), 10);

        PianoRollNote note = Assert.Single(pianoRoll.RollNotes);
        TranscriptEntryViewModel entry = liveTuner.Transcript.First(e => e.Type == "Note");
        Assert.Equal(entry.ConfidenceMean, note.Confidence);
        Assert.Equal(entry.CentsDeviation, note.CentsDeviation);
    }

    [Fact]
    public void Moving_KeepsDetectionData_UnlessThePitchChanges()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.ReplaceAll(new[] { new PianoRollNote(Guid.NewGuid(), 60, 0, 1, Confidence: 0.9, CentsDeviation: 4) });
        Guid id = pianoRoll.RollNotes[0].Id;

        pianoRoll.MoveNote(id, 2, 60);
        Assert.Equal((0.9, 4.0), (pianoRoll.RollNotes[0].Confidence, pianoRoll.RollNotes[0].CentsDeviation));
        pianoRoll.ResizeNote(id, 2, 3);
        Assert.Equal(0.9, pianoRoll.RollNotes[0].Confidence);

        pianoRoll.MoveNote(id, 2, 62);
        Assert.Null(pianoRoll.RollNotes[0].Confidence);
        Assert.Null(pianoRoll.RollNotes[0].CentsDeviation);

        pianoRoll.UndoCommand.Execute(null);
        Assert.Equal(0.9, pianoRoll.RollNotes[0].Confidence);
    }

    [Fact]
    public void ChordVoices_AreNamed_AndOnlyTheRootKeepsItsDetectionData()
    {
        var pianoRoll = CreateStandaloneRoll();
        pianoRoll.ReplaceAll(new[] { new PianoRollNote(Guid.NewGuid(), 62, 0, 1, Confidence: 0.8, CentsDeviation: -2) });

        IReadOnlyList<PianoRollNote> voices = pianoRoll.ApplyChordTransform(new[] { pianoRoll.RollNotes[0].Id }, ChordQuality.Minor);

        Assert.All(voices, v => Assert.Equal("D Minor", v.ChordName));
        Assert.Equal(0.8, voices.Single(v => v.MidiNumber == 62).Confidence);
        Assert.All(voices.Where(v => v.MidiNumber != 62), v => Assert.Null(v.Confidence));
    }

    [Fact]
    public void DrawnNotes_HaveNoDetectionData()
    {
        PianoRollNote drawn = CreateStandaloneRoll().AddNote(0, 1, 60);
        Assert.Null(drawn.Confidence);
        Assert.Null(drawn.ChordName);
    }
}
