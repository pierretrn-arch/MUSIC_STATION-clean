using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ToneDetector.App.Controls;
using ToneDetector.App.ViewModels;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>The shared piano roll control, driven through its <see cref="PianoRollViewModel"/>.
/// Geometry for these tests: 72px key column, 40px ruler, 20 beats across 400px (20px a beat),
/// fixed 24px rows (A0-C8) with 20 rows in view, centred on the notes.</summary>
public class PianoRollControlTests
{
    private const double GridWidth = 400;
    private const double PxPerBeat = 20;

    private sealed record Harness(PianoRollControl Control, PianoRollViewModel Roll, PlaybackViewModel Engine, FakeSynth Synth);

    private static Harness Create(params (int Midi, double Start, double Length)[] notes)
    {
        var synth = new FakeSynth();
        var engine = new PlaybackViewModel(() => synth, () => new FakeWavePlayer(), runClock: false);
        var roll = new PianoRollViewModel(engine) { PixelsPerBeat = PxPerBeat };
        roll.ReplaceAll(notes.Select(n => new PianoRollNote(Guid.NewGuid(), n.Midi, n.Start, n.Length)));
        var control = new PianoRollControl { Editor = roll };
        control.ArrangeForTest(72 + GridWidth, 40 + 20 * PianoRollControl.RowHeightPx);
        control.ScrollToNotes();
        return new Harness(control, roll, engine, synth);
    }

    private static Point At(PianoRollControl control, double beat, int midi) =>
        new(control.XForBeatForTest(beat), control.YForMidiForTest(midi));

    // ---- Bars ----

    [Fact]
    public void Editor_AddsOneBarPerInRangeNote() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1), (64, 1, 1), (20, 0, 1)); // 20 is below A0

        Assert.Equal(2, h.Control.BarCountForTest);
    });

    [Fact]
    public void AddingAndReplacingNotes_UpdatesTheBars() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1));

        h.Roll.AddNote(2, 1, 62);
        Assert.Equal(2, h.Control.BarCountForTest);

        h.Roll.ReplaceAll(Array.Empty<PianoRollNote>());
        Assert.Equal(0, h.Control.BarCountForTest);
    });

    [Fact]
    public void AnEdit_KeepsTheViewAndTheSelection_ButALoadResetsTheView() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1), (64, 30, 1));
        h.Control.ViewStartBeats = 12;
        PianoRollNote first = h.Roll.RollNotes[0];
        h.Control.SetSelection(new[] { first.Id });

        h.Roll.ResizeNote(first.Id, 0, 2);

        Assert.Equal(12.0, h.Control.ViewStartBeats);
        Assert.Equal(new[] { first.Id }, h.Control.SelectedNoteIds);

        h.Roll.ReplaceAll(new[] { new PianoRollNote(Guid.NewGuid(), 60, 0, 1) });
        Assert.Equal(0.0, h.Control.ViewStartBeats);
        Assert.Empty(h.Control.SelectedNoteIds);
    });

    [Fact]
    public void ViewStart_CannotGoBeforeBarOne() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1));
        h.Control.ViewStartBeats = -8;
        Assert.Equal(0.0, h.Control.ViewStartBeats);
    });

    [Fact]
    public void ScrollRange_ReachesPastTheLastNote() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 40, 2));

        // The last note ends at beat 42; the default loop runs to the end of its bar (44), plus a bar, minus the view.
        Assert.Equal(20.0, h.Control.VisibleBeats);
        Assert.Equal(44 + 4 - 20, h.Control.ScrollMaximumBeats);

        h.Control.ScrollToBeat(16);
        Assert.Equal(16.0, h.Control.ViewStartBeats);
    });

    // ---- Hit-testing (overlap-aware) ----

    [Fact]
    public void HitTestNoteBars_ReturnsTopmostFirst() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 4), (60, 1, 4)); // same row, overlapping; the second is on top
        PianoRollNote first = h.Roll.RollNotes[0], second = h.Roll.RollNotes[1];

        IReadOnlyList<Guid> candidates = h.Control.HitTestNoteBarsForTest(At(h.Control, 2, 60));

        Assert.Equal(new[] { second.Id, first.Id }, candidates);
    });

    [Fact]
    public void RepeatedClickAtTheSameSpot_CyclesToTheNoteUnderneath() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 4), (60, 1, 4));
        PianoRollNote first = h.Roll.RollNotes[0], second = h.Roll.RollNotes[1];
        Point spot = At(h.Control, 2, 60);

        Assert.Equal(second.Id, h.Control.SimulateClickForTest(spot));
        Assert.Equal(first.Id, h.Control.SimulateClickForTest(spot));
        Assert.Equal(second.Id, h.Control.SimulateClickForTest(spot));
        Assert.Equal(second.Id, h.Control.SimulateClickForTest(new Point(spot.X + 10, spot.Y)));
    });

    [Fact]
    public void Bars_SitOnTheirBeats() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 4, 2));

        Assert.Equal(72 + 4 * PxPerBeat, h.Control.XForBeatForTest(4));
        Assert.Single(h.Control.HitTestNoteBarsForTest(At(h.Control, 5, 60)));
        Assert.Empty(h.Control.HitTestNoteBarsForTest(At(h.Control, 3.5, 60)));
        Assert.Empty(h.Control.HitTestNoteBarsForTest(At(h.Control, 5, 61)));
    });

    // ---- Key column ----

    [Fact]
    public void HitTestKeyboardNote_MapsRowsToKeys() => StaTestRunner.Run(() =>
    {
        Harness h = Create();
        h.Control.ViewTop = 0;
        Assert.Equal(108, h.Control.HitTestKeyboardNoteForTest(41));      // top row: C8
        Assert.Equal(89, h.Control.HitTestKeyboardNoteForTest(40 + 479)); // 20th row in view
        Assert.Null(h.Control.HitTestKeyboardNoteForTest(20));            // in the ruler

        h.Control.ViewTop = h.Control.VerticalScrollMaximum;              // scrolled to the bottom
        Assert.Equal(21, h.Control.HitTestKeyboardNoteForTest(40 + 479)); // A0
        Assert.Equal(40, h.Control.HitTestKeyboardNoteForTest(41));
    });

    [Fact]
    public void PressingKeys_FiresNoteOnAndOff() => StaTestRunner.Run(() =>
    {
        Harness h = Create();
        var ons = new List<(int, int)>();
        var offs = new List<int>();
        h.Control.NoteOn += (n, v) => ons.Add((n, v));
        h.Control.NoteOff += offs.Add;

        h.Control.SimulateKeyboardPressForTest(60);
        h.Control.SimulateKeyboardPressForTest(60); // same key again: nothing new
        h.Control.SimulateKeyboardPressForTest(62);
        h.Control.Stop();

        Assert.Equal(new[] { (60, 100), (62, 100) }, ons);
        Assert.Equal(new[] { 60, 62 }, offs);
        Assert.Null(h.Control.PressedKeyboardNoteForTest);
    });

    // ---- Sounding keys and notes ----

    [Fact]
    public void PlaybackNotes_LightTheirKeysAndGetAnOutline() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1), (67, 2, 1));
        PianoRollNote playing = h.Roll.RollNotes[0], later = h.Roll.RollNotes[1];

        h.Engine.Play(h.Roll);

        Assert.True(h.Control.IsSoundingForTest(playing.Id));
        Assert.False(h.Control.IsSoundingForTest(later.Id));
        Assert.True(h.Control.IsKeySoundingForTest(60));
        Assert.Equal(Color.FromRgb(0xF4, 0xF2, 0xEE), ((SolidColorBrush)h.Control.BarStrokeForTest(playing.Id)!).Color); // InkStrong
        Assert.Null(h.Control.BarStrokeForTest(later.Id));

        h.Engine.StopPlayback();
        Assert.False(h.Control.IsSoundingForTest(playing.Id));
        Assert.False(h.Control.IsKeySoundingForTest(60));
    });

    [Fact]
    public void HeldInputKeys_LightUpToo() => StaTestRunner.Run(() =>
    {
        Harness h = Create();
        var keyboard = new FakeNoteInputSource();
        h.Engine.AttachInputSource(keyboard);

        keyboard.RaiseNoteOn(64, 100);

        Assert.True(h.Control.IsKeySoundingForTest(64));
    });

    // ---- Selection ----

    [Fact]
    public void Marquee_SelectsNotesInTheBox() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1), (62, 1, 1), (72, 1, 1), (60, 8, 1));
        var raised = 0;
        h.Control.SelectionChanged += (_, _) => raised++;

        h.Control.SimulateMarqueeForTest(0.5, 59, 2, 64);

        Assert.Equal(new HashSet<Guid> { h.Roll.RollNotes[0].Id, h.Roll.RollNotes[1].Id }, h.Control.SelectedNoteIds.ToHashSet());
        Assert.True(raised > 0);

        h.Control.SimulateMarqueeForTest(7, 60, 9, 60, additive: true);
        Assert.Equal(3, h.Control.SelectedNoteIds.Count);
    });

    [Fact]
    public void SelectAll_SelectsEveryNote() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1), (62, 1, 1));
        h.Control.SelectAll();
        Assert.Equal(2, h.Control.SelectedNoteIds.Count);
    });

    [Fact]
    public void SelectedNotes_ArePitchFilledWithTheSelectionRing() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1));
        PianoRollNote note = h.Roll.RollNotes[0];

        h.Control.SetSelection(new[] { note.Id });

        Assert.Equal(Color.FromRgb(0xDC, 0xEF, 0xFB), ((SolidColorBrush)h.Control.BarStrokeForTest(note.Id)!).Color); // SelectionRing
    });

    // ---- Keyboard shortcuts ----

    [Theory]
    [InlineData(Key.V, EditTool.Select)]
    [InlineData(Key.D, EditTool.Draw)]
    [InlineData(Key.E, EditTool.Erase)]
    [InlineData(Key.S, EditTool.Split)]
    public void ToolKeys_PickTheTool(Key key, EditTool expected) => StaTestRunner.Run(() =>
    {
        Harness h = Create();
        h.Roll.Tool = EditTool.Split == expected ? EditTool.Draw : EditTool.Split;

        Assert.True(h.Control.HandleShortcut(key, ModifierKeys.None));
        Assert.Equal(expected, h.Roll.Tool);
    });

    [Fact]
    public void Delete_DeletesTheSelection_AsOneUndoStep() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1), (62, 1, 1), (64, 2, 1));
        h.Control.SetSelection(h.Roll.RollNotes.Take(2).Select(n => n.Id));

        h.Control.HandleShortcut(Key.Delete, ModifierKeys.None);
        Assert.Equal(64, Assert.Single(h.Roll.RollNotes).MidiNumber);

        h.Control.HandleShortcut(Key.Z, ModifierKeys.Control);
        Assert.Equal(3, h.Roll.RollNotes.Count);

        h.Control.HandleShortcut(Key.Y, ModifierKeys.Control);
        Assert.Single(h.Roll.RollNotes);
        h.Control.HandleShortcut(Key.Z, ModifierKeys.Control);
        h.Control.HandleShortcut(Key.Z, ModifierKeys.Control | ModifierKeys.Shift); // redo
        Assert.Single(h.Roll.RollNotes);
    });

    [Fact]
    public void CopyAndPaste_PastesAtThePlayhead_AndSelectsThePastedNotes() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1), (64, 1, 1));
        h.Control.HandleShortcut(Key.A, ModifierKeys.Control);
        h.Control.HandleShortcut(Key.C, ModifierKeys.Control);
        h.Roll.PositionBeats = 8;

        h.Control.HandleShortcut(Key.V, ModifierKeys.Control);

        Assert.Equal(4, h.Roll.RollNotes.Count);
        Assert.Equal(2, h.Control.SelectedNoteIds.Count);
        Assert.All(h.Control.SelectedNoteIds, id => Assert.True(h.Roll.RollNotes.Single(n => n.Id == id).StartBeat >= 8));
    });

    [Fact]
    public void Cut_RemovesTheSelection() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1), (64, 1, 1));
        h.Control.SetSelection(new[] { h.Roll.RollNotes[0].Id });

        h.Control.HandleShortcut(Key.X, ModifierKeys.Control);

        Assert.Equal(64, Assert.Single(h.Roll.RollNotes).MidiNumber);
        Assert.True(h.Roll.CanPaste);
    });

    [Fact]
    public void Escape_ClearsTheSelection_AndAltCombosAreLeftAlone() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1));
        h.Control.SelectAll();

        Assert.False(h.Control.HandleShortcut(Key.D, ModifierKeys.Alt));
        Assert.True(h.Control.HandleShortcut(Key.Escape, ModifierKeys.None));
        Assert.Empty(h.Control.SelectedNoteIds);
    });

    [Fact]
    public void WhileRecording_ShortcutsDoNotEdit() => StaTestRunner.Run(() =>
    {
        Harness h = Create((60, 0, 1));
        h.Control.SelectAll();
        h.Control.IsLive = true;

        h.Control.HandleShortcut(Key.Delete, ModifierKeys.None);
        h.Control.HandleShortcut(Key.X, ModifierKeys.Control);

        Assert.Single(h.Roll.RollNotes);
    });

    // ---- Pure helpers ----

    [Fact]
    public void ColorForPitchClass_ReturnsTwelveDistinctColors()
    {
        var colors = Enumerable.Range(0, 12).Select(PianoRollControl.ColorForPitchClass).ToList();

        Assert.Equal(12, colors.Distinct().Count());
    }

    [Fact]
    public void ColorForPitchClass_WrapsForOutOfRangeInput()
    {
        Assert.Equal(PianoRollControl.ColorForPitchClass(0), PianoRollControl.ColorForPitchClass(12));
        Assert.Equal(PianoRollControl.ColorForPitchClass(11), PianoRollControl.ColorForPitchClass(-1));
    }
}
