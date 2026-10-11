using NAudio.Wave;
using ToneDetector.Audio.Synth;

namespace ToneDetector.Tests;

/// <summary>Test double for an <see cref="ISynth"/> that also owns unmanaged-style resources (like
/// <see cref="VstHostSynth"/> owning a loaded native plugin), so <see cref="ToneDetector.App.ViewModels.PlaybackViewModel"/>'s
/// synth-swap logic can be verified to actually dispose the outgoing synth when it's disposable.</summary>
public sealed class FakeDisposableSynth : ISynth, IDisposable
{
    private readonly SineSynthesizer _placeholderOutput = new(); // never Read() from in these tests; just satisfies the interface

    public bool WasDisposed { get; private set; }

    public ISampleProvider Output => _placeholderOutput.Output;

    public void NoteOn(int midiNote, int velocity) { }
    public void NoteOff(int midiNote) { }
    public void AllNotesOff() { }

    public void Dispose() => WasDisposed = true;
}
