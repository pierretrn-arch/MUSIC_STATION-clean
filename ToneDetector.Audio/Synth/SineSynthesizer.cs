using NAudio.Wave;
using ToneDetector.Core.Pitch;

namespace ToneDetector.Audio.Synth;

/// <summary>
/// Simple polyphonic sine-wave synth: the app's first <see cref="ISynth"/> implementation, good
/// enough to hear back captured phrases and play live from a keyboard without needing a real
/// instrument/plugin. Each voice is one sine oscillator with a short linear attack/release so
/// notes don't click; oldest-voice-stealing keeps a fixed, bounded voice pool.
/// </summary>
public sealed class SineSynthesizer : ISynth, ISampleProvider
{
    private const int SynthSampleRate = 48000;
    private const double AttackSeconds = 0.005;
    private const double ReleaseSeconds = 0.030;

    private enum VoiceState { Idle, Attack, Sustain, Release }

    private sealed class Voice
    {
        public int MidiNote;
        public double Amplitude;
        public double PhaseRadians;
        public double PhaseIncrement;
        public VoiceState State = VoiceState.Idle;
        public double EnvelopeLevel;
        public long NoteOnClock;
    }

    private readonly object _lock = new();
    private readonly Voice[] _voices;
    private long _sampleClock;

    public WaveFormat WaveFormat { get; }
    public ISampleProvider Output => this;

    /// <summary>Size of the voice pool.</summary>
    public int MaxVoices => _voices.Length;

    public SineSynthesizer(int maxVoices = 16)
    {
        if (maxVoices < 1)
            throw new ArgumentOutOfRangeException(nameof(maxVoices), "At least one voice is required.");

        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(SynthSampleRate, channels: 1);
        _voices = new Voice[maxVoices];
        for (int i = 0; i < maxVoices; i++)
            _voices[i] = new Voice();
    }

    public void NoteOn(int midiNote, int velocity)
    {
        lock (_lock)
        {
            double amplitude = Math.Clamp(velocity, 0, 127) / 127.0;
            double frequencyHz = NoteQuantizer.AbsoluteCentsToFrequency(midiNote * 100.0);

            Voice voice = FindVoiceForNoteOn();
            voice.MidiNote = midiNote;
            voice.Amplitude = amplitude;
            voice.PhaseIncrement = 2.0 * Math.PI * frequencyHz / SynthSampleRate;
            voice.PhaseRadians = 0.0;
            voice.EnvelopeLevel = 0.0;
            voice.State = VoiceState.Attack;
            voice.NoteOnClock = _sampleClock;
        }
    }

    public void NoteOff(int midiNote)
    {
        lock (_lock)
        {
            foreach (Voice voice in _voices)
            {
                if (voice.MidiNote == midiNote && voice.State is VoiceState.Attack or VoiceState.Sustain)
                    voice.State = VoiceState.Release;
            }
        }
    }

    public void AllNotesOff()
    {
        lock (_lock)
        {
            foreach (Voice voice in _voices)
            {
                voice.State = VoiceState.Idle;
                voice.EnvelopeLevel = 0.0;
            }
        }
    }

    private Voice FindVoiceForNoteOn()
    {
        foreach (Voice voice in _voices)
        {
            if (voice.State == VoiceState.Idle)
                return voice;
        }

        // No free voice: steal the oldest one (lowest NoteOnClock), regardless of its current
        // envelope stage — a wide voice pool (default 16) makes this a rare edge case, not the
        // common path, so a clean crossfade isn't worth the extra complexity here.
        Voice oldest = _voices[0];
        foreach (Voice voice in _voices)
        {
            if (voice.NoteOnClock < oldest.NoteOnClock)
                oldest = voice;
        }
        return oldest;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            for (int i = 0; i < count; i++)
            {
                double mixed = 0.0;
                foreach (Voice voice in _voices)
                {
                    if (voice.State == VoiceState.Idle)
                        continue;

                    AdvanceEnvelope(voice);
                    mixed += Math.Sin(voice.PhaseRadians) * voice.Amplitude * voice.EnvelopeLevel;

                    voice.PhaseRadians += voice.PhaseIncrement;
                    if (voice.PhaseRadians >= 2.0 * Math.PI)
                        voice.PhaseRadians -= 2.0 * Math.PI;
                }

                // Soft-clip rather than hard-clip so stacking many voices saturates gently instead
                // of harshly distorting.
                buffer[offset + i] = (float)Math.Tanh(mixed);
                _sampleClock++;
            }
        }

        return count;
    }

    private static void AdvanceEnvelope(Voice voice)
    {
        switch (voice.State)
        {
            case VoiceState.Attack:
                voice.EnvelopeLevel += 1.0 / (AttackSeconds * SynthSampleRate);
                if (voice.EnvelopeLevel >= 1.0)
                {
                    voice.EnvelopeLevel = 1.0;
                    voice.State = VoiceState.Sustain;
                }
                break;

            case VoiceState.Release:
                voice.EnvelopeLevel -= 1.0 / (ReleaseSeconds * SynthSampleRate);
                if (voice.EnvelopeLevel <= 0.0)
                {
                    voice.EnvelopeLevel = 0.0;
                    voice.State = VoiceState.Idle;
                }
                break;
        }
    }

    /// <summary>Number of voices not currently idle — test-only visibility into the voice pool
    /// without exposing internal synthesis state as public API.</summary>
    internal int ActiveVoiceCountForTest
    {
        get
        {
            lock (_lock)
                return _voices.Count(v => v.State != VoiceState.Idle);
        }
    }

    internal IReadOnlyList<int> ActiveMidiNotesForTest
    {
        get
        {
            lock (_lock)
                return _voices.Where(v => v.State != VoiceState.Idle).Select(v => v.MidiNote).ToList();
        }
    }
}
