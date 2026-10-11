using NAudio.Wave;

namespace ToneDetector.Audio.Synth;

/// <summary>
/// Minimal playback engine contract: note events in, an audio stream out. Kept deliberately small
/// so a future VST3 host can implement it as a drop-in replacement for <see cref="SineSynthesizer"/>
/// without any change to the input side (MIDI keyboard, computer keyboard, on-screen keyboard, or
/// transcript playback) — they all only ever talk to <see cref="ISynth"/>, never a concrete type.
/// </summary>
public interface ISynth
{
    void NoteOn(int midiNote, int velocity);
    void NoteOff(int midiNote);
    void AllNotesOff();
    ISampleProvider Output { get; }
}
