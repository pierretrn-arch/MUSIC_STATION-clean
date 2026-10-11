// PHASE4-VST3-NATIVE.md Milestone 5: minimal ISampleProvider wrapping the Milestone 4 VstShim
// API - matching the shape VstHostSynth (Milestone 7, ToneDetector.Audio/Synth/) will eventually
// need to implement ISynth.Output. Deliberately thin: NoteOn/NoteOff just forward to the shim's
// own internal event queue (already thread-safe, see vstshim.cpp) and Read just pulls rendered
// frames straight from VstShim_Process - no extra buffering/state kept on the C# side.
using NAudio.Wave;

namespace CSharpRoundTripTest;

internal sealed class VstShimSampleProvider : ISampleProvider, IDisposable
{
    private readonly IntPtr _handle;
    private readonly int _channels;
    private bool _disposed;

    public WaveFormat WaveFormat { get; }

    /// <summary>Longest a single Read() call has taken, in milliseconds - the crude glitch-risk
    /// signal used by the self-test below (a Read that takes longer than the real-time duration
    /// of audio it produced means NAudio's playback thread couldn't keep up, which is exactly
    /// what causes an audible glitch/underrun).</summary>
    public double WorstReadMillis { get; private set; }

    /// <summary>When set, every Read() also appends its rendered samples here - lets the
    /// self-test capture what actually played through NAudio for independent verification
    /// (peak/RMS spot checks), without needing a human to listen for that part.</summary>
    public List<float>? Recording { get; set; }

    public VstShimSampleProvider(IntPtr handle, int sampleRate, int channels)
    {
        _handle = handle;
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
    }

    public void NoteOn(int midiNote, int velocity) => VstShimInterop.VstShim_NoteOn(_handle, midiNote, velocity);
    public void NoteOff(int midiNote) => VstShimInterop.VstShim_NoteOff(_handle, midiNote);
    public void AllNotesOff() => VstShimInterop.VstShim_AllNotesOff(_handle);

    public unsafe int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _channels;
        if (frames <= 0)
            return 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int framesRendered;
        fixed (float* ptr = &buffer[offset])
            framesRendered = VstShimInterop.VstShim_Process(_handle, ptr, frames);
        sw.Stop();

        double budgetMs = 1000.0 * frames / WaveFormat.SampleRate;
        if (sw.Elapsed.TotalMilliseconds > WorstReadMillis)
            WorstReadMillis = sw.Elapsed.TotalMilliseconds;
        if (sw.Elapsed.TotalMilliseconds > budgetMs)
            Console.WriteLine($"  [glitch risk] Read took {sw.Elapsed.TotalMilliseconds:F2}ms for {budgetMs:F2}ms of audio ({frames} frames)");

        int samplesRendered = framesRendered < 0 ? 0 : framesRendered * _channels;
        if (Recording is not null && samplesRendered > 0)
            Recording.AddRange(new ReadOnlySpan<float>(buffer, offset, samplesRendered));

        return samplesRendered;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        VstShimInterop.VstShim_UnloadPlugin(_handle);
    }
}
