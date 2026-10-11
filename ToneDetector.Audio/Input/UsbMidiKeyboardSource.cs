using NAudio.Midi;

namespace ToneDetector.Audio.Input;

/// <summary>
/// Wraps NAudio's <see cref="MidiIn"/> for a single MIDI input device (enumerated separately from
/// the audio capture device picker — MIDI and audio devices are entirely different device lists).
/// </summary>
public sealed class UsbMidiKeyboardSource : INoteInputSource, IDisposable
{
    private readonly int _deviceIndex;
    private MidiIn? _midiIn;

    public event Action<int, int>? NoteOn;
    public event Action<int>? NoteOff;

    public UsbMidiKeyboardSource(int deviceIndex)
    {
        _deviceIndex = deviceIndex;
    }

    public static IReadOnlyList<(int Index, string Name)> GetAvailableDevices()
    {
        var devices = new List<(int, string)>();
        for (int i = 0; i < MidiIn.NumberOfDevices; i++)
            devices.Add((i, MidiIn.DeviceInfo(i).ProductName));
        return devices;
    }

    public void Start()
    {
        if (_midiIn is not null)
            return;

        _midiIn = new MidiIn(_deviceIndex);
        _midiIn.MessageReceived += OnMessageReceived;
        _midiIn.Start();
    }

    public void Stop()
    {
        if (_midiIn is null)
            return;

        _midiIn.Stop();
        _midiIn.MessageReceived -= OnMessageReceived;
        _midiIn.Dispose();
        _midiIn = null;
    }

    private void OnMessageReceived(object? sender, MidiInMessageEventArgs e) => Dispatch(e.MidiEvent);

    /// <summary>Core message-routing logic, split out from <see cref="OnMessageReceived"/> so it's
    /// directly unit-testable against real (but not device-backed) <see cref="NoteEvent"/>/
    /// <see cref="NoteOnEvent"/> instances, without needing an actual MIDI device.</summary>
    internal void Dispatch(MidiEvent midiEvent)
    {
        switch (midiEvent)
        {
            // NoteOnEvent covers CommandCode.NoteOn messages; per MIDI convention a "Note On"
            // with velocity 0 is equivalent to a Note Off (common on hardware that avoids sending
            // real Note Off status bytes via running status), so it's routed as one here too.
            case NoteOnEvent noteOn when noteOn.Velocity > 0:
                NoteOn?.Invoke(noteOn.NoteNumber, noteOn.Velocity);
                break;
            case NoteOnEvent noteOn:
                NoteOff?.Invoke(noteOn.NoteNumber);
                break;
            case NoteEvent noteOff when noteOff.CommandCode == MidiCommandCode.NoteOff:
                NoteOff?.Invoke(noteOff.NoteNumber);
                break;
        }
    }

    public void Dispose() => Stop();
}
