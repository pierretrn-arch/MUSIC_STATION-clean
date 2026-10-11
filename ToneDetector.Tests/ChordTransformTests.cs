using ToneDetector.App.ViewModels;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class ChordTransformTests
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

    [Fact]
    public void ApplyChordTransform_ReplacesSingleNoteWithChordVoicesSharingOneGroupId()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // ~C4 (MIDI 60)
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);     // commits the note

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);
        Assert.Equal(60, original.MidiNumber);

        IReadOnlyList<TranscriptEntryViewModel> newVoices =
            viewModel.ApplyChordTransform(new[] { original }, ChordQuality.Minor);

        Assert.Equal(3, newVoices.Count); // Minor = 3 voices
        Assert.Equal(3, viewModel.Transcript.Count);
        Assert.Equal(new[] { 60, 63, 67 }, viewModel.Transcript.Select(e => e.MidiNumber));

        Guid? groupId = viewModel.Transcript[0].ChordGroupId;
        Assert.NotNull(groupId);
        Assert.All(viewModel.Transcript, e => Assert.Equal(groupId, e.ChordGroupId));

        // Same start/duration as the original across every voice.
        Assert.All(viewModel.Transcript, e => Assert.Equal(original.StartTimeMs, e.StartTimeMs));
        Assert.All(viewModel.Transcript, e => Assert.Equal(original.DurationMs, e.DurationMs));
    }

    [Fact]
    public void ApplyChordTransform_OnMultipleSelectedNotes_TransformsEachIndependently()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(130.81), 10); // ~C3 (MIDI 48)
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        FeedFrames(viewModel, MakeVoicedFrame(146.83), 10); // ~D3 (MIDI 50)
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);

        var noteRows = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, noteRows.Count);
        Assert.Equal(48, noteRows[0].MidiNumber);
        Assert.Equal(50, noteRows[1].MidiNumber);

        viewModel.ApplyChordTransform(noteRows, ChordQuality.Major);

        var resultingNotes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(6, resultingNotes.Count); // 2 roots x 3 voices, transformed independently

        // Each root's own triad, not a combined chord across both roots.
        Assert.Equal(new[] { 48, 52, 55 }, resultingNotes.Take(3).Select(e => e.MidiNumber));
        Assert.Equal(new[] { 50, 54, 57 }, resultingNotes.Skip(3).Take(3).Select(e => e.MidiNumber));

        Guid? group1 = resultingNotes[0].ChordGroupId;
        Guid? group2 = resultingNotes[3].ChordGroupId;
        Assert.NotEqual(group1, group2);
    }

    [Fact]
    public void UndoLastTransform_RestoresExactOriginalNote()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);
        double originalStart = original.StartTimeMs;
        double originalDuration = original.DurationMs;
        int originalMidi = original.MidiNumber;

        Assert.False(viewModel.CanUndoEdit);
        viewModel.ApplyChordTransform(new[] { original }, ChordQuality.Diminished);
        Assert.True(viewModel.CanUndoEdit);
        Assert.Equal(3, viewModel.Transcript.Count);

        viewModel.UndoLastEditCommand.Execute(null);

        TranscriptEntryViewModel restored = Assert.Single(viewModel.Transcript);
        Assert.Equal(originalMidi, restored.MidiNumber);
        Assert.Equal(originalStart, restored.StartTimeMs);
        Assert.Equal(originalDuration, restored.DurationMs);
        Assert.Null(restored.ChordGroupId);
        Assert.False(viewModel.CanUndoEdit);
    }

    [Fact]
    public void UndoLastTransform_AfterMultiNoteTransform_RestoresBothOriginalNotesAtCorrectPositions()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(130.81), 10); // C3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        FeedFrames(viewModel, MakeVoicedFrame(146.83), 10); // D3
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);

        var noteRows = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        viewModel.ApplyChordTransform(noteRows, ChordQuality.Dominant7th); // 4 voices each

        viewModel.UndoLastEditCommand.Execute(null);

        var restoredNotes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, restoredNotes.Count);
        Assert.Equal(48, restoredNotes[0].MidiNumber);
        Assert.Equal(50, restoredNotes[1].MidiNumber);
        Assert.All(restoredNotes, e => Assert.Null(e.ChordGroupId));
    }

    // ---- Regression tests: chord voices sharing one StartTimeMs must not clamp each other ----
    // (see LiveTunerViewModel.FindMinStart/FindMaxEnd) - MoveOrResizeEntry/AddEntry key off the
    // "immediate neighbor" in the sorted _committedEvents list, and a chord's 2nd/3rd voice sits at
    // that exact same StartTimeMs as the voice being edited - without skipping past ties, resizing or
    // drawing near a chord collapsed the allowed duration down to MinNoteDurationMs (20ms) regardless
    // of how far the user actually dragged.

    [Fact]
    public void MoveOrResizeEntry_OnAChordVoice_CanGrowFreely_NotClampedBySiblingVoices()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);     // commits the note
        viewModel.IsRecording = false;

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);
        IReadOnlyList<TranscriptEntryViewModel> voices = viewModel.ApplyChordTransform(new[] { original }, ChordQuality.Minor);
        Assert.Equal(3, voices.Count);

        // Nothing else follows this chord in the transcript, so resizing its first voice should be
        // free to grow as large as requested - before the fix, this clamped to voice[1] (its own
        // sibling, tied at the same StartTimeMs) and collapsed to 20ms instead.
        TranscriptEntryViewModel? updated = viewModel.MoveOrResizeEntry(voices[0], voices[0].StartTimeMs, newDurationMs: 5000.0);

        Assert.NotNull(updated);
        Assert.Equal(5000.0, updated!.DurationMs, precision: 3);
    }

    [Fact]
    public void MoveOrResizeEntry_OnAChordVoice_GrowingIntoAGenuinelyLaterNote_ShrinksItInsteadOfClamping()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 6);      // 120ms dead zone - no Silence reported
        FeedFrames(viewModel, MakeVoicedFrame(293.66), 10); // D4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);     // flush D4 via real trailing silence
        viewModel.IsRecording = false;

        var notes = viewModel.Transcript.Where(e => e.Type == "Note").ToList();
        Assert.Equal(2, notes.Count);
        double secondNoteStartBefore = notes[1].StartTimeMs;
        double secondNoteEndBefore = notes[1].StartTimeMs + notes[1].DurationMs;

        IReadOnlyList<TranscriptEntryViewModel> voices = viewModel.ApplyChordTransform(new[] { notes[0] }, ChordQuality.Minor);

        // The fix only skips ties (same-StartTimeMs siblings) - a genuinely later, distinct-time note
        // is still a real neighbor, and growing into it now cascades (shrinks it) same as the plain
        // non-chord case, rather than being rejected.
        TranscriptEntryViewModel? updated = viewModel.MoveOrResizeEntry(voices[0], voices[0].StartTimeMs, newDurationMs: 100000.0);

        Assert.NotNull(updated);
        double updatedEndMs = updated!.StartTimeMs + updated.DurationMs;
        Assert.True(updatedEndMs > secondNoteStartBefore);

        TranscriptEntryViewModel neighborAfter = viewModel.Transcript[3]; // 3 chord voices (index 0-2) then the neighbor
        Assert.Equal(updatedEndMs, neighborAfter.StartTimeMs, precision: 3);
        Assert.Equal(20.0, neighborAfter.DurationMs, precision: 3); // MinNoteDurationMs
        Assert.Equal(secondNoteEndBefore, neighborAfter.StartTimeMs + neighborAfter.DurationMs, precision: 3);
    }

    [Fact]
    public void AddEntry_AtTheSameStartTimeAsAnExistingChord_IsNotClampedBySiblingVoices()
    {
        var viewModel = CreateViewModel();
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // C4
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        viewModel.IsRecording = false;

        TranscriptEntryViewModel original = Assert.Single(viewModel.Transcript);
        IReadOnlyList<TranscriptEntryViewModel> voices = viewModel.ApplyChordTransform(new[] { original }, ChordQuality.Minor);
        double chordStartMs = voices[0].StartTimeMs;

        // Drawing a brand-new voice into an existing chord's own time slot must not be clamped down
        // by the existing voices tied at that same StartTimeMs.
        TranscriptEntryViewModel? added = viewModel.AddEntry(chordStartMs, durationMs: 5000.0, midiNumber: 72);

        Assert.NotNull(added);
        Assert.Equal(5000.0, added!.DurationMs, precision: 3);
    }
}
