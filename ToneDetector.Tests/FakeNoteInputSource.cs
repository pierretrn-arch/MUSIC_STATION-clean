using ToneDetector.Audio.Input;

namespace ToneDetector.Tests;

/// <summary>Test double for <see cref="INoteInputSource"/>, letting tests simulate note events
/// from an arbitrary "device" without any real hardware/keyboard/mouse involved.</summary>
public sealed class FakeNoteInputSource : INoteInputSource
{
    public bool WasStarted { get; private set; }
    public bool WasStopped { get; private set; }

    public event Action<int, int>? NoteOn;
    public event Action<int>? NoteOff;

    public void Start() => WasStarted = true;

    public void Stop() => WasStopped = true;

    public void RaiseNoteOn(int midiNote, int velocity) => NoteOn?.Invoke(midiNote, velocity);

    public void RaiseNoteOff(int midiNote) => NoteOff?.Invoke(midiNote);
}
