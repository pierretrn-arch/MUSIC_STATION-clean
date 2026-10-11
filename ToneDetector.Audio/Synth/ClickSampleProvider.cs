using NAudio.Wave;

namespace ToneDetector.Audio.Synth;

/// <summary>
/// The metronome's tick, mixed into the synth's output on its way to the speakers (so a VST
/// instrument never plays it): a short, fast-decaying sine burst, higher on beat 1 of a bar than
/// on beats 2-4. Passes the synth through untouched until <see cref="Trigger"/>; a new tick
/// replaces one still ringing.
/// </summary>
public sealed class ClickSampleProvider : ISampleProvider
{
    public const double BeatFrequencyHz = 1000.0;
    public const double AccentFrequencyHz = 1600.0;
    public const double DurationSeconds = 0.035;
    private const float Level = 0.5f;

    private readonly ISampleProvider _source;
    private readonly object _gate = new();
    private int _samplesLeft;
    private int _samplePosition;
    private double _frequencyHz;

    public ClickSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Starts a tick now (callable from any thread).</summary>
    public void Trigger(bool accent)
    {
        lock (_gate)
        {
            _frequencyHz = accent ? AccentFrequencyHz : BeatFrequencyHz;
            _samplePosition = 0;
            _samplesLeft = (int)(WaveFormat.SampleRate * DurationSeconds);
        }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = Math.Max(1, WaveFormat.Channels);
        int sampleRate = WaveFormat.SampleRate;
        lock (_gate)
        {
            if (_samplesLeft <= 0)
                return read;

            // The tick needs the whole buffer even if the synth had nothing to say.
            for (int i = read; i < count; i++)
                buffer[offset + i] = 0f;

            for (int frame = 0; frame < count / channels && _samplesLeft > 0; frame++)
            {
                double t = _samplePosition / (double)sampleRate;
                double envelope = Math.Exp(-t / (DurationSeconds / 5.0));
                float value = (float)(Level * envelope * Math.Sin(2 * Math.PI * _frequencyHz * t));
                for (int c = 0; c < channels; c++)
                    buffer[offset + frame * channels + c] += value;
                _samplePosition++;
                _samplesLeft--;
            }
        }
        return count;
    }
}
