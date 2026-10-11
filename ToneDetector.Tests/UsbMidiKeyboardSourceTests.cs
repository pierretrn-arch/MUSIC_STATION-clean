using NAudio.Midi;
using ToneDetector.Audio.Input;
using Xunit;

namespace ToneDetector.Tests;

public class UsbMidiKeyboardSourceTests
{
    [Fact]
    public void Dispatch_NoteOnWithPositiveVelocity_RaisesNoteOn()
    {
        var source = new UsbMidiKeyboardSource(deviceIndex: 0);
        (int MidiNote, int Velocity)? received = null;
        source.NoteOn += (note, velocity) => received = (note, velocity);

        source.Dispatch(new NoteOnEvent(absoluteTime: 0, channel: 1, noteNumber: 60, velocity: 100, duration: 0));

        Assert.Equal((60, 100), received);
    }

    [Fact]
    public void Dispatch_NoteOnWithZeroVelocity_IsTreatedAsNoteOff()
    {
        var source = new UsbMidiKeyboardSource(deviceIndex: 0);
        int? noteOnFired = null;
        int? noteOffFired = null;
        source.NoteOn += (note, _) => noteOnFired = note;
        source.NoteOff += note => noteOffFired = note;

        source.Dispatch(new NoteOnEvent(absoluteTime: 0, channel: 1, noteNumber: 60, velocity: 0, duration: 0));

        Assert.Null(noteOnFired);
        Assert.Equal(60, noteOffFired);
    }

    [Fact]
    public void Dispatch_RealNoteOffEvent_RaisesNoteOff()
    {
        var source = new UsbMidiKeyboardSource(deviceIndex: 0);
        int? received = null;
        source.NoteOff += note => received = note;

        source.Dispatch(new NoteEvent(absoluteTime: 0, channel: 1, MidiCommandCode.NoteOff, noteNumber: 64, velocity: 0));

        Assert.Equal(64, received);
    }

    [Fact]
    public void Dispatch_NonNoteEvent_RaisesNeitherEvent()
    {
        var source = new UsbMidiKeyboardSource(deviceIndex: 0);
        bool anyFired = false;
        source.NoteOn += (_, _) => anyFired = true;
        source.NoteOff += _ => anyFired = true;

        source.Dispatch(new ControlChangeEvent(absoluteTime: 0, channel: 1, MidiController.MainVolume, controllerValue: 64));

        Assert.False(anyFired);
    }
}
