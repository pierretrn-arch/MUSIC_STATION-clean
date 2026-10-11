using System.Windows.Input;
using ToneDetector.App.Controls;
using ToneDetector.App.ViewModels;
using ToneDetector.App.Views;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Phase 6b Stage 1: tracks in a tab - the model, its Undo, mixing in playback, the
/// Results rows, and the roll's scrolling.</summary>
public class TrackTests
{
    private static PlaybackViewModel CreateEngine(FakeSynth synth) =>
        new(() => synth, () => new FakeWavePlayer(), runClock: false);

    private static PianoRollNote Note(int midi, double start, double length = 1) =>
        new(Guid.NewGuid(), midi, start, length, Velocity: 100);

    private static PianoRollViewModel TwoTracks(PlaybackViewModel engine)
    {
        var roll = new PianoRollViewModel(engine);
        roll.ReplaceTracks(new[]
        {
            new TrackSeed("Piano", new[] { Note(60, 0) }),
            new TrackSeed("Bass", new[] { Note(40, 0) }),
        });
        return roll;
    }

    // ---- Model ----

    [Fact]
    public void ANewRoll_HasOneEmptyTrack_NamedForTheTab() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine);

        Track track = Assert.Single(roll.Tracks);
        Assert.Equal("Track 1", track.Name);
        Assert.Same(track, roll.SelectedTrack);
        Assert.True(track.IsSelected);
        Assert.Same(track.Notes, roll.RollNotes);
        Assert.Equal(0, track.ColourIndex);
    });

    [Fact]
    public void ReplaceTracks_MakesOneTrackPerSeed_ColoursInOrder_FirstSelected() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);

        Assert.Equal(new[] { "Piano", "Bass" }, roll.Tracks.Select(t => t.Name));
        Assert.Equal(new[] { 0, 1 }, roll.Tracks.Select(t => t.ColourIndex));
        Assert.Same(roll.Tracks[0], roll.SelectedTrack);
        Assert.Equal(2, roll.AllNotes.Count());
        Assert.False(roll.CanUndoEdit);
    });

    [Fact]
    public void TrackColours_RepeatAfterEight() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine);
        for (int i = 0; i < 8; i++)
            roll.AddTrack();

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 0 }, roll.Tracks.Select(t => t.ColourIndex));
    });

    [Fact]
    public void Edits_GoToTheSelectedTrackOnly() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);

        roll.SelectedTrack = roll.Tracks[1];
        roll.AddNote(4, 1, 43);

        Assert.Single(roll.Tracks[0].Notes);
        Assert.Equal(2, roll.Tracks[1].Notes.Count);
        Assert.Same(roll.Tracks[1].Notes, roll.RollNotes);
    });

    [Fact]
    public void UndoOfANoteEdit_SelectsTheTrackItWasMadeIn() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);
        roll.SelectedTrack = roll.Tracks[1];
        roll.AddNote(4, 1, 43);
        roll.SelectedTrack = roll.Tracks[0];

        roll.UndoCommand.Execute(null);

        Assert.Same(roll.Tracks[1], roll.SelectedTrack);
        Assert.Single(roll.Tracks[1].Notes);

        roll.SelectedTrack = roll.Tracks[0];
        roll.RedoCommand.Execute(null);
        Assert.Same(roll.Tracks[1], roll.SelectedTrack);
        Assert.Equal(2, roll.Tracks[1].Notes.Count);
    });

    [Fact]
    public void AddTrack_IsUndoable_AndSelectsTheNewTrack() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);

        Track added = roll.AddTrack();
        Assert.Equal(3, roll.Tracks.Count);
        Assert.Same(added, roll.SelectedTrack);
        Assert.Equal("Track 3", added.Name);
        Assert.Empty(added.Notes);

        roll.UndoCommand.Execute(null);
        Assert.Equal(2, roll.Tracks.Count);
        Assert.Contains(roll.SelectedTrack, roll.Tracks);

        roll.RedoCommand.Execute(null);
        Assert.Same(added, roll.SelectedTrack);
    });

    [Fact]
    public void DeleteTrack_IsUndoable_ButTheLastTrackStays() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);
        Track bass = roll.Tracks[1];
        roll.SelectedTrack = bass;

        Assert.True(roll.DeleteTrack(bass));
        Assert.Single(roll.Tracks);
        Assert.Same(roll.Tracks[0], roll.SelectedTrack);
        Assert.False(roll.CanDeleteTrack);
        Assert.False(roll.DeleteTrack(roll.Tracks[0]));
        Assert.Single(roll.Tracks);

        roll.UndoCommand.Execute(null);
        Assert.Equal(2, roll.Tracks.Count);
        Assert.Same(bass, roll.Tracks[1]);
        Assert.Same(bass, roll.SelectedTrack);
        Assert.Single(bass.Notes);
    });

    [Fact]
    public void RenameAndColour_AreUndoable_AndBlankNamesAreIgnored() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);
        Track piano = roll.Tracks[0];

        roll.RenameTrack(piano, "  Keys  ");
        roll.RenameTrack(piano, "   ");
        roll.SetTrackColour(piano, 5);
        Assert.Equal("Keys", piano.Name);
        Assert.Equal(5, piano.ColourIndex);

        roll.SelectedTrack = roll.Tracks[1];
        roll.UndoCommand.Execute(null);
        Assert.Equal(0, piano.ColourIndex);
        Assert.Same(piano, roll.SelectedTrack);
        roll.UndoCommand.Execute(null);
        Assert.Equal("Piano", piano.Name);
        Assert.False(roll.CanUndoEdit);
    });

    [Fact]
    public void MuteSoloVolumePan_AreNotInUndo() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);

        roll.Tracks[0].IsMuted = true;
        roll.Tracks[1].IsSolo = true;
        roll.Tracks[1].Volume = 0.5;
        roll.Tracks[1].Pan = -20;

        Assert.False(roll.CanUndoEdit);
        Assert.Equal("L20", roll.Tracks[1].PanText);
    });

    [Fact]
    public void AnySolo_OnlySoloedTracksPlay_AndMutedStaysSilentEvenIfSoloed() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);
        Track piano = roll.Tracks[0], bass = roll.Tracks[1];

        Assert.Equal(2, roll.AudibleNotes().Count);

        bass.IsSolo = true;
        Assert.Equal(new[] { bass }, roll.AudibleNotes().Select(n => n.Track));

        bass.IsMuted = true;
        Assert.Empty(roll.AudibleNotes());

        bass.IsSolo = false;
        Assert.Equal(new[] { piano }, roll.AudibleNotes().Select(n => n.Track));
    });

    [Fact]
    public void ConfirmReplaceTracks_OnlyAsksWhenThereAreNotes() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine);
        var asked = new List<string>();
        roll.Confirm = question => { asked.Add(question); return false; };

        Assert.True(roll.ConfirmReplaceTracks());
        Assert.Empty(asked);

        roll.AddNote(0, 1, 60);
        Assert.False(roll.ConfirmReplaceTracks());
        Assert.Equal("Replace your current tracks? They aren't saved.", Assert.Single(asked));
    });

    [Fact]
    public void ExportTracks_WritesEveryTrack_WhateverMuteAndSoloSay() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);
        roll.Tracks[0].IsMuted = true;
        roll.Tracks[1].IsSolo = true;

        IReadOnlyList<TrackExport> tracks = roll.ExportTracks();

        Assert.Equal(new[] { "Piano", "Bass" }, tracks.Select(t => t.Name));
        Assert.True(tracks[0].IsMuted);
        Assert.True(tracks[1].IsSolo);
        Assert.Equal(TrackPalette.Colours[1], tracks[1].Colour);
    });

    [Fact]
    public void InstrumentName_IsShownOnEveryTrack() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = TwoTracks(engine);

        roll.InstrumentName = "Surge XT";
        Track added = roll.AddTrack();

        Assert.All(roll.Tracks, t => Assert.Equal("Surge XT", t.Instrument));
        Assert.Equal(new[] { "Surge XT" }, added.InstrumentChoices);
    });

    [Fact]
    public void EndText_AndTheDefaultLoop_CoverEveryTrack() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine);
        roll.ReplaceTracks(new[]
        {
            new TrackSeed("A", new[] { Note(60, 0) }),
            new TrackSeed("B", new[] { Note(62, 6, 1) }),
        });

        Assert.Equal(MusicalTime.FormatPosition(7), roll.EndText);
        Assert.Equal(8.0, roll.LoopEndBeat);
    });

    [Fact]
    public void Notation_FollowsTheSelectedTrack() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine) { Bpm = 120 };
        roll.ReplaceTracks(new[]
        {
            new TrackSeed("A", new[] { Note(60, 1) }),
            new TrackSeed("B", new[] { Note(62, 0), Note(64, 0), Note(65, 2) }),
        });

        Assert.Equal(new[] { "Silence", "Note" }, roll.NotationEntries.Select(e => e.Type));

        roll.SelectedTrack = roll.Tracks[1];
        // The chord's top note only (one voice), a rest, then F4.
        Assert.Equal(new[] { "Note", "Silence", "Note" }, roll.NotationEntries.Select(e => e.Type));
        Assert.Equal(64, roll.NotationEntries[0].MidiNumber);
        Assert.Equal(500.0, roll.NotationEntries[1].DurationMs, 6);
    });

    [Fact]
    public void Zoom_IsClamped() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine);

        roll.PixelsPerBeat = 100000;
        Assert.Equal(PianoRollViewModel.MaxPixelsPerBeat, roll.PixelsPerBeat);
        roll.PixelsPerBeat = double.NaN;
        Assert.Equal(PianoRollViewModel.DefaultPixelsPerBeat, roll.PixelsPerBeat);
    });

    // ---- Playback: mute/solo, shared-synth volume, input ----

    [Fact]
    public void Play_MixesEveryAudibleTrack() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = TwoTracks(engine);

        engine.Play(roll);

        Assert.Equal(new[] { 40, 60 }, synth.NoteOnCalls.Select(c => c.Note).OrderBy(n => n));
    });

    [Fact]
    public void Play_LeavesOutMutedTracks_AndMutingMidPlayStopsTheirNotes() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        var roll = new PianoRollViewModel(engine);
        roll.ReplaceTracks(new[]
        {
            new TrackSeed("A", new[] { Note(60, 0, 4) }),
            new TrackSeed("B", new[] { Note(40, 0, 4) }),
        });
        roll.Tracks[1].IsMuted = true;

        engine.Play(roll);
        Assert.Equal(60, Assert.Single(synth.NoteOnCalls).Note);

        roll.Tracks[0].IsMuted = true;
        Assert.Contains(60, synth.NoteOffCalls);
    });

    [Fact]
    public void WithASharedSynth_TrackVolumeScalesVelocity_AndSilentSkipsTheNote() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = TwoTracks(engine);
        roll.Tracks[0].Volume = MixerMath.DbToPosition(-6.0206); // half the level
        roll.Tracks[1].Volume = 0; // silent

        engine.Play(roll);

        (int note, int velocity) = Assert.Single(synth.NoteOnCalls);
        Assert.Equal(60, note);
        Assert.Equal(50, velocity);
        Assert.False(engine.HasTrackVoices);
    });

    [Fact]
    public void KeyboardInput_PlaysThroughTheInputTabsSelectedTrack() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = TwoTracks(engine);
        roll.Tracks[1].Volume = MixerMath.DbToPosition(-6.0206);
        roll.SelectedTrack = roll.Tracks[1];
        engine.InputRoll = roll;
        var source = new FakeNoteInputSource();
        engine.AttachInputSource(source);

        source.RaiseNoteOn(64, 100);
        source.RaiseNoteOff(64);

        Assert.Equal((64, 50), Assert.Single(synth.NoteOnCalls));
        Assert.Contains(64, synth.NoteOffCalls);
    });

    [Fact]
    public void TheBuiltInSynth_GivesEachTrackItsOwnVoice() => StaTestRunner.Run(() =>
    {
        using var engine = new PlaybackViewModel(null, () => new FakeWavePlayer(), runClock: false);
        PianoRollViewModel roll = TwoTracks(engine);

        engine.Play(roll);

        Assert.True(engine.HasTrackVoices);
        Assert.True(engine.IsPlaying);
        engine.StopPlayback();
    });

    // ---- Results (File Analysis) ----

    [Fact]
    public void ResultRows_ShowADash_ForNotesWithoutDetectionData() =>
        StaTestRunner.Run(() =>
        {
            var detected = new PianoRollNote(Guid.NewGuid(), 69, 0, 1, Confidence: 0.9, CentsDeviation: 5, MeanF0Hz: 441.27);
            var drawn = new PianoRollNote(Guid.NewGuid(), 71, 2, 1, Origin: PianoRollNoteOrigin.Drawn);

            Assert.Equal(2, AnalysisResultRow.Build(new[] { drawn, detected }, 120).Count); // silence hidden by default
            IReadOnlyList<AnalysisResultRow> rows = AnalysisResultRow.Build(new[] { drawn, detected }, 120, hideSilence: false);

            Assert.Equal(new[] { "Note", "Silence", "Note" }, rows.Select(r => r.TypeText));
            Assert.Equal("0.90", rows[0].ConfidenceText);
            Assert.Equal("441.27", rows[0].MeanF0Text);
            Assert.Equal("500", rows[1].StartText);
            Assert.Equal("500", rows[1].DurationText);
            Assert.Equal("—", rows[2].ConfidenceText);
            Assert.Equal("—", rows[2].MeanF0Text);
            Assert.Equal("—", rows[2].CentsText);
            Assert.Null(rows[2].Confidence);
        });

    [Fact]
    public void SplittingOrRepitchingANote_DropsItsMeanF0()
    {
        var note = new PianoRollNote(Guid.NewGuid(), 69, 0, 2, Confidence: 0.9, MeanF0Hz: 440);

        (PianoRollNote left, PianoRollNote right) = RollEditing.Split(note, 1)!.Value;

        Assert.Null(left.MeanF0Hz);
        Assert.Null(right.MeanF0Hz);
    }

    [Fact]
    public void FileAnalysisResults_FollowTheSelectedTrack() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var fileAnalysis = new FileAnalysisViewModel(new PipelineSettingsViewModel(), engine);
        fileAnalysis.Roll.ReplaceTracks(new[]
        {
            new TrackSeed("A", new[] { Note(60, 0) }),
            new TrackSeed("B", new[] { Note(62, 0), Note(64, 1) }),
        });

        Assert.Single(fileAnalysis.Results);
        fileAnalysis.Roll.SelectedTrack = fileAnalysis.Roll.Tracks[1];
        Assert.Equal(2, fileAnalysis.Results.Count);
        Assert.Equal(new[] { "D4", "E4" }, fileAnalysis.Results.Select(r => r.NoteText));
    });

    // ---- Roll control: ghosts and scrolling ----

    [Fact]
    public void TheRoll_OnlyMakesBarsForTheSelectedTrack() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine);
        roll.ReplaceTracks(new[]
        {
            new TrackSeed("A", new[] { Note(60, 0) }),
            new TrackSeed("B", new[] { Note(62, 0), Note(64, 1) }),
        });
        var control = new PianoRollControl { Editor = roll };
        control.ArrangeForTest(472, 40 + 20 * PianoRollControl.RowHeightPx);

        Assert.Equal(1, control.BarCountForTest);
        roll.SelectedTrack = roll.Tracks[1];
        Assert.Equal(2, control.BarCountForTest);
    });

    [Fact]
    public void ArrowKeys_ScrollTheView_AndNeverMoveNotes() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine);
        roll.ReplaceAll(new[] { Note(60, 0), Note(62, 40) });
        var control = new PianoRollControl { Editor = roll };
        control.ArrangeForTest(472, 40 + 20 * PianoRollControl.RowHeightPx);
        control.ScrollToNotes();
        control.SetSelection(roll.RollNotes.Select(n => n.Id));
        List<PianoRollNote> before = roll.RollNotes.ToList();
        double top = control.ViewTop;

        Assert.True(control.ScrollByKey(Key.Right));
        Assert.Equal(1.0, control.ViewStartBeats);
        Assert.True(control.ScrollByKey(Key.Down));
        Assert.Equal(top + PianoRollControl.RowHeightPx, control.ViewTop);
        Assert.True(control.ScrollByKey(Key.Left));
        Assert.Equal(0.0, control.ViewStartBeats);

        Assert.Equal(before, roll.RollNotes);
        Assert.False(roll.CanUndoEdit);
    });

    [Fact]
    public void ViewTop_StaysInsideTheKeyRange() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var control = new PianoRollControl { Editor = new PianoRollViewModel(engine) };
        control.ArrangeForTest(472, 40 + 20 * PianoRollControl.RowHeightPx);

        control.ViewTop = -50;
        Assert.Equal(0.0, control.ViewTop);
        control.ViewTop = 1e6;
        Assert.Equal(control.VerticalScrollMaximum, control.ViewTop);
        Assert.Equal((88 - 20) * PianoRollControl.RowHeightPx, control.VerticalScrollMaximum, 6);
    });

    [Fact]
    public void ArrowKeys_BelongToControlsThatUseThem()
    {
        Assert.True(PianoRollShortcuts.IsArrow(Key.Up));
        Assert.False(PianoRollShortcuts.IsArrow(Key.A));
        Assert.False(PianoRollShortcuts.UsesArrowKeys(null));
    }

    [Fact]
    public void PanControl_ArrowsMoveItByFive()
    {
        Assert.Equal(-PanControl.KeyStep, PanControl.StepFor(Key.Left));
        Assert.Equal(PanControl.KeyStep, PanControl.StepFor(Key.Right));
        Assert.Null(PanControl.StepFor(Key.Up));
        Assert.Equal(5, PanControl.KeyStep);
    }
}
