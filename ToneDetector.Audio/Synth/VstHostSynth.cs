using NAudio.Wave;

namespace ToneDetector.Audio.Synth;

/// <summary>
/// <see cref="ISynth"/> implementation backed by a real VST3 plugin, hosted through the native
/// shim proven out in PHASE4-VST3-NATIVE.md Milestones 1-6 (<c>VstNativeShim/</c>). This is
/// exactly the drop-in replacement <see cref="SineSynthesizer"/>'s doc comment anticipated:
/// <see cref="PlaybackViewModel"/> and every <c>INoteInputSource</c> only ever talk to
/// <see cref="ISynth"/>, so nothing on the input side needed to change for this to work.
/// Editor embedding (<see cref="HasEditor"/>/<see cref="OpenEditor"/>/<see cref="CloseEditor"/>) is
/// deliberately NOT part of <see cref="ISynth"/> - it's VST3-specific, with no equivalent for
/// <see cref="SineSynthesizer"/>, so callers that want it check for a concrete <see cref="VstHostSynth"/>.
/// </summary>
public sealed class VstHostSynth : ISynth, ISampleProvider, IDisposable
{
    // Matches SineSynthesizer's own fixed sample rate - NAudio's output device adapts to whatever
    // WaveFormat is reported, so this doesn't need to match the OS/device's native rate.
    private const double SynthSampleRate = 48000.0;
    private const int BlockSize = 512;

    private readonly IntPtr _handle;
    private bool _disposed;

    public string PluginPath { get; }
    public WaveFormat WaveFormat { get; }
    public ISampleProvider Output => this;

    /// <summary>True if the plugin exposes a separate controller class with its own GUI - queried
    /// once at load time since it can't change for the lifetime of a loaded plugin instance.</summary>
    public bool HasEditor { get; }

    public VstHostSynth(string pluginPath)
    {
        PluginPath = pluginPath;

        _handle = VstNativeInterop.VstShim_LoadPlugin(pluginPath);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"Failed to load VST3 plugin '{pluginPath}': {VstNativeInterop.GetLastError(IntPtr.Zero)}");

        if (VstNativeInterop.VstShim_Initialize(_handle, SynthSampleRate, BlockSize) == 0)
        {
            string error = VstNativeInterop.GetLastError(_handle);
            VstNativeInterop.VstShim_UnloadPlugin(_handle);
            throw new InvalidOperationException($"Failed to initialize VST3 plugin '{pluginPath}': {error}");
        }

        int channels = VstNativeInterop.VstShim_GetOutputChannelCount(_handle);
        if (channels <= 0)
        {
            VstNativeInterop.VstShim_UnloadPlugin(_handle);
            throw new InvalidOperationException($"VST3 plugin '{pluginPath}' reports 0 output channels.");
        }

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat((int)SynthSampleRate, channels);
        HasEditor = VstNativeInterop.VstShim_HasEditor(_handle) != 0;
    }

    public void NoteOn(int midiNote, int velocity) => VstNativeInterop.VstShim_NoteOn(_handle, midiNote, velocity);
    public void NoteOff(int midiNote) => VstNativeInterop.VstShim_NoteOff(_handle, midiNote);
    public void AllNotesOff() => VstNativeInterop.VstShim_AllNotesOff(_handle);

    public unsafe int Read(float[] buffer, int offset, int count)
    {
        int channels = WaveFormat.Channels;
        int frames = count / channels;
        if (frames <= 0)
            return 0;

        int framesRendered;
        fixed (float* ptr = &buffer[offset])
            framesRendered = VstNativeInterop.VstShim_Process(_handle, ptr, frames);

        return framesRendered < 0 ? 0 : framesRendered * channels;
    }

    /// <summary>Embeds the plugin's own editor view into <paramref name="parentHwnd"/> (a native
    /// child window the caller already created, e.g. via a WPF <c>HwndHost</c>). Returns false on
    /// failure - check <see cref="GetLastError"/> for why. No-ops safely (returns false) if
    /// <see cref="HasEditor"/> is false.</summary>
    public bool OpenEditor(IntPtr parentHwnd, out int width, out int height)
    {
        if (!HasEditor)
        {
            width = 0;
            height = 0;
            return false;
        }
        return VstNativeInterop.VstShim_OpenEditor(_handle, parentHwnd, out width, out height) != 0;
    }

    public void CloseEditor() => VstNativeInterop.VstShim_CloseEditor(_handle);

    public string GetLastError() => VstNativeInterop.GetLastError(_handle);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        VstNativeInterop.VstShim_UnloadPlugin(_handle);
    }
}
