using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ToneDetector.Audio.Synth;

/// <summary>
/// Mixes one built-in synth voice per track into a stereo stream: each track's
/// <see cref="SineSynthesizer"/> goes through its own pan and volume, then everything is summed.
/// Voices are made on first use (<see cref="VoiceFor"/>) and can be removed when their track goes.
/// Centre pan leaves both channels at full level, so a single track sounds as loud as the old
/// single synth did.
/// </summary>
public sealed class TrackMixer : ISampleProvider
{
    private sealed record Voice(SineSynthesizer Synth, PanningSampleProvider Pan, VolumeSampleProvider Volume);

    private readonly object _lock = new();
    private readonly Dictionary<Guid, Voice> _voices = new();
    private readonly MixingSampleProvider _mixer;
    private readonly int _voicesPerTrack;
    private readonly int _sampleRate;

    public TrackMixer(int voicesPerTrack = 16)
    {
        _voicesPerTrack = Math.Max(1, voicesPerTrack);
        _sampleRate = new SineSynthesizer(1).WaveFormat.SampleRate;
        _mixer = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(_sampleRate, 2)) { ReadFully = true };
    }

    public WaveFormat WaveFormat => _mixer.WaveFormat;

    public int VoicesPerTrack => _voicesPerTrack;

    public IReadOnlyCollection<Guid> TrackIds
    {
        get
        {
            lock (_lock)
                return _voices.Keys.ToList();
        }
    }

    /// <summary>The synth for a track, made (at the given level and pan) if it doesn't exist yet.</summary>
    public ISynth VoiceFor(Guid trackId, float gain = 1f, float pan = 0f)
    {
        lock (_lock)
        {
            if (_voices.TryGetValue(trackId, out Voice? voice))
                return voice.Synth;

            var synth = new SineSynthesizer(_voicesPerTrack);
            var panning = new PanningSampleProvider(synth) { PanStrategy = new StereoBalanceStrategy(), Pan = ClampPan(pan) };
            var volume = new VolumeSampleProvider(panning) { Volume = Math.Max(0f, gain) };
            voice = new Voice(synth, panning, volume);
            _voices[trackId] = voice;
            _mixer.AddMixerInput(volume);
            return synth;
        }
    }

    /// <summary>Sets a track's level (linear gain) and pan (-1 left to 1 right), if it has a voice.</summary>
    public void SetMix(Guid trackId, float gain, float pan)
    {
        lock (_lock)
        {
            if (!_voices.TryGetValue(trackId, out Voice? voice))
                return;
            voice.Volume.Volume = Math.Max(0f, gain);
            voice.Pan.Pan = ClampPan(pan);
        }
    }

    public void Remove(Guid trackId)
    {
        lock (_lock)
        {
            if (!_voices.Remove(trackId, out Voice? voice))
                return;
            voice.Synth.AllNotesOff();
            _mixer.RemoveMixerInput(voice.Volume);
        }
    }

    public void AllNotesOff()
    {
        lock (_lock)
        {
            foreach (Voice voice in _voices.Values)
                voice.Synth.AllNotesOff();
        }
    }

    public int Read(float[] buffer, int offset, int count) => _mixer.Read(buffer, offset, count);

    private static float ClampPan(float pan) => float.IsFinite(pan) ? Math.Clamp(pan, -1f, 1f) : 0f;
}
