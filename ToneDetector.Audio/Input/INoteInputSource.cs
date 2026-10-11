namespace ToneDetector.Audio.Input;

/// <summary>
/// A source of note events — a USB MIDI keyboard, the computer's own keyboard, or an on-screen
/// virtual keyboard all implement this the same way, so a playback router can subscribe to any
/// number of them simultaneously and forward every event straight to whichever <c>ISynth</c> is
/// active without caring which physical source it came from.
/// </summary>
public interface INoteInputSource
{
    /// <summary>(midiNote, velocity).</summary>
    event Action<int, int>? NoteOn;

    /// <summary>(midiNote).</summary>
    event Action<int>? NoteOff;

    void Start();
    void Stop();
}
