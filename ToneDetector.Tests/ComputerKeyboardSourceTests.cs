using System.Windows.Input;
using ToneDetector.App.Input;
using Xunit;

namespace ToneDetector.Tests;

public class ComputerKeyboardSourceTests
{
    [Theory]
    [InlineData(Key.Z, 48)]  // C3 (base)
    [InlineData(Key.S, 49)]  // C#3
    [InlineData(Key.X, 50)]  // D3
    [InlineData(Key.M, 59)]  // B3
    [InlineData(Key.Q, 60)]  // C4 (upper row starts one octave above base)
    [InlineData(Key.P, 76)]  // E5
    public void HandleKeyDown_MapsKeyToCorrectMidiNote(Key key, int expectedMidiNote) => StaTestRunner.Run(() =>
    {
        var source = new ComputerKeyboardSource(new System.Windows.Window());
        (int Note, int Velocity)? received = null;
        source.NoteOn += (note, velocity) => received = (note, velocity);

        bool handled = source.HandleKeyDown(key, isFocusInTextInput: false);

        Assert.True(handled);
        Assert.Equal(expectedMidiNote, received?.Note);
    });

    [Fact]
    public void HandleKeyDown_WhileFocusInTextInput_DoesNothing() => StaTestRunner.Run(() =>
    {
        var source = new ComputerKeyboardSource(new System.Windows.Window());
        bool fired = false;
        source.NoteOn += (_, _) => fired = true;

        bool handled = source.HandleKeyDown(Key.Z, isFocusInTextInput: true);

        Assert.False(handled);
        Assert.False(fired);
    });

    [Fact]
    public void HandleKeyDown_KeyRepeat_DoesNotFireDuplicateNoteOn() => StaTestRunner.Run(() =>
    {
        var source = new ComputerKeyboardSource(new System.Windows.Window());
        int fireCount = 0;
        source.NoteOn += (_, _) => fireCount++;

        source.HandleKeyDown(Key.Z, isFocusInTextInput: false);
        source.HandleKeyDown(Key.Z, isFocusInTextInput: false); // OS key-repeat while still held
        source.HandleKeyDown(Key.Z, isFocusInTextInput: false);

        Assert.Equal(1, fireCount);
    });

    [Fact]
    public void HandleKeyUp_AfterKeyDown_FiresNoteOffForTheSameNote() => StaTestRunner.Run(() =>
    {
        var source = new ComputerKeyboardSource(new System.Windows.Window());
        int? noteOff = null;
        source.NoteOff += note => noteOff = note;

        source.HandleKeyDown(Key.X, isFocusInTextInput: false);
        bool handled = source.HandleKeyUp(Key.X);

        Assert.True(handled);
        Assert.Equal(50, noteOff); // D3
    });

    [Fact]
    public void HandleKeyUp_WithoutPriorKeyDown_DoesNothing() => StaTestRunner.Run(() =>
    {
        var source = new ComputerKeyboardSource(new System.Windows.Window());
        bool fired = false;
        source.NoteOff += _ => fired = true;

        bool handled = source.HandleKeyUp(Key.X);

        Assert.False(handled);
        Assert.False(fired);
    });

    [Fact]
    public void OctaveShift_PageUpAndPageDown_ChangeSubsequentNoteMapping() => StaTestRunner.Run(() =>
    {
        var source = new ComputerKeyboardSource(new System.Windows.Window());
        var receivedNotes = new List<int>();
        source.NoteOn += (note, _) => receivedNotes.Add(note);

        source.HandleKeyDown(Key.Z, isFocusInTextInput: false); // 48 at default base
        source.HandleKeyUp(Key.Z);

        source.HandleKeyDown(Key.PageUp, isFocusInTextInput: false);
        source.HandleKeyDown(Key.Z, isFocusInTextInput: false); // should now be 60
        source.HandleKeyUp(Key.Z);

        source.HandleKeyDown(Key.PageDown, isFocusInTextInput: false);
        source.HandleKeyDown(Key.PageDown, isFocusInTextInput: false);
        source.HandleKeyDown(Key.Z, isFocusInTextInput: false); // back down two octaves from 60: 36
        source.HandleKeyUp(Key.Z);

        Assert.Equal(new[] { 48, 60, 36 }, receivedNotes);
    });

    [Fact]
    public void Stop_ReleasesAnyStillHeldNotes() => StaTestRunner.Run(() =>
    {
        var source = new ComputerKeyboardSource(new System.Windows.Window());
        var noteOffs = new List<int>();
        source.NoteOff += note => noteOffs.Add(note);

        source.HandleKeyDown(Key.Z, isFocusInTextInput: false);
        source.HandleKeyDown(Key.X, isFocusInTextInput: false);

        source.Stop();

        Assert.Contains(48, noteOffs);
        Assert.Contains(50, noteOffs);
    });
}
