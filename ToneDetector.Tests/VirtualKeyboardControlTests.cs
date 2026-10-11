using System.Windows;
using ToneDetector.App.Controls;
using Xunit;

namespace ToneDetector.Tests;

public class VirtualKeyboardControlTests
{
    /// <summary>Forces a real ActualWidth/ActualHeight via a manual layout pass (Measure+Arrange),
    /// without needing a live Window/visual tree — a standard technique for unit-testing WPF
    /// layout-dependent logic like hit-testing.</summary>
    private static VirtualKeyboardControl CreateSizedControl(int minNote, int maxNote, double width = 700, double height = 100)
    {
        var control = new VirtualKeyboardControl { MinNote = minNote, MaxNote = maxNote };
        control.Measure(new Size(width, height));
        control.Arrange(new Rect(0, 0, width, height));
        return control;
    }

    [Fact]
    public void HitTestNote_OnLeftmostWhiteKey_ReturnsMinNote() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72, width: 700, height: 100); // C4..C5, 8 white keys

        int? note = control.HitTestNote(new Point(2, 90)); // near the bottom-left, clear of any black key

        Assert.Equal(60, note); // C4
    });

    [Fact]
    public void HitTestNote_OnRightmostWhiteKey_ReturnsMaxNote() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72, width: 700, height: 100);

        int? note = control.HitTestNote(new Point(698, 90));

        Assert.Equal(72, note); // C5
    });

    [Fact]
    public void HitTestNote_InUpperBlackKeyZone_ReturnsBlackKeyNote() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72, width: 700, height: 100); // 8 white keys => 87.5px each
        // The first black key (C#4=61) sits centered at the boundary after the 1st white key (x = 87.5).
        int? note = control.HitTestNote(new Point(87.5, 10)); // near the top, within the black-key zone

        Assert.Equal(61, note); // C#4
    });

    [Fact]
    public void SimulateMouseDown_FiresNoteOnForTheHitKey() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72);
        (int Note, int Velocity)? received = null;
        control.NoteOn += (note, velocity) => received = (note, velocity);

        control.SimulateMouseDown(new Point(2, 90));

        Assert.Equal(60, received?.Note);
    });

    [Fact]
    public void SimulateMouseUp_FiresNoteOffForTheCurrentlyPressedKey() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72);
        int? noteOff = null;
        control.NoteOff += note => noteOff = note;

        control.SimulateMouseDown(new Point(2, 90));
        control.SimulateMouseUp();

        Assert.Equal(60, noteOff);
    });

    [Fact]
    public void SimulateMouseMove_AcrossKeyBoundary_GlissandosNoteOffThenNoteOn() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72, width: 700); // 87.5px per white key
        var events = new List<string>();
        control.NoteOn += (note, _) => events.Add($"On:{note}");
        control.NoteOff += note => events.Add($"Off:{note}");

        control.SimulateMouseDown(new Point(2, 90));      // key 0 -> note 60
        control.SimulateMouseMove(new Point(100, 90));    // moves into key 1 -> note 62 (D4)

        Assert.Equal(new[] { "On:60", "Off:60", "On:62" }, events);
    });

    [Fact]
    public void SimulateMouseMove_WithoutMouseDown_DoesNotPlayNotes() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72);
        bool fired = false;
        control.NoteOn += (_, _) => fired = true;

        control.SimulateMouseMove(new Point(2, 90));

        Assert.False(fired);
    });

    [Fact]
    public void SimulateMouseLeave_WhileHeld_ReleasesTheCurrentNote() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72);
        int? noteOff = null;
        control.NoteOff += note => noteOff = note;

        control.SimulateMouseDown(new Point(2, 90));
        control.SimulateMouseLeave();

        Assert.Equal(60, noteOff);
    });

    [Fact]
    public void SimulateMouseMove_OnSameKey_DoesNotRefireNoteOn() => StaTestRunner.Run(() =>
    {
        var control = CreateSizedControl(minNote: 60, maxNote: 72);
        int fireCount = 0;
        control.NoteOn += (_, _) => fireCount++;

        control.SimulateMouseDown(new Point(2, 90));
        control.SimulateMouseMove(new Point(5, 92)); // still within the same key
        control.SimulateMouseMove(new Point(10, 88));

        Assert.Equal(1, fireCount);
    });
}
