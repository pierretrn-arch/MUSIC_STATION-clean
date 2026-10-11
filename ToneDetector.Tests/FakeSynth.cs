using NAudio.Wave;
using ToneDetector.Audio.Synth;

namespace ToneDetector.Tests;

/// <summary>Test double for <see cref="ISynth"/> that records every call instead of actually
/// synthesizing audio, for verifying routing/playback-scheduling logic in isolation.</summary>
public sealed class FakeSynth : ISynth
{
    private readonly SineSynthesizer _placeholderOutput = new(); // never Read() from in these tests; just satisfies the interface

    public List<(int Note, int Velocity)> NoteOnCalls { get; } = new();
    public List<int> NoteOffCalls { get; } = new();
    public int AllNotesOffCallCount { get; private set; }

    public ISampleProvider Output => _placeholderOutput.Output;

    public void NoteOn(int midiNote, int velocity) => NoteOnCalls.Add((midiNote, velocity));

    public void NoteOff(int midiNote) => NoteOffCalls.Add(midiNote);

    public void AllNotesOff() => AllNotesOffCallCount++;
}
