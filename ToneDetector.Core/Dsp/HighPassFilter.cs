namespace ToneDetector.Core.Dsp;

/// <summary>
/// User-configurable high-pass filter stage (e.g. to remove DC offset, mic rumble, or mains
/// hum before pitch extraction). Backed by an RBJ-cookbook biquad section.
/// </summary>
public sealed class HighPassFilter : IAudioStage
{
    private readonly BiquadFilter _biquad;
    private int _sampleRate;
    private double _q;

    public bool Enabled { get; set; } = true;

    public double CutoffHz { get; private set; }

    public HighPassFilter(double cutoffHz, int sampleRate, double q = 0.7071)
    {
        CutoffHz = cutoffHz;
        _sampleRate = sampleRate;
        _q = q;
        _biquad = BiquadFilter.HighPass(cutoffHz, sampleRate, q);
    }

    public void SetCutoff(double cutoffHz)
    {
        CutoffHz = cutoffHz;
        _biquad.ConfigureHighPass(cutoffHz, _sampleRate, _q);
    }

    public void Process(Span<float> samples)
    {
        if (!Enabled)
            return;

        for (int i = 0; i < samples.Length; i++)
            samples[i] = _biquad.ProcessSample(samples[i]);
    }

    public void Reset() => _biquad.Reset();
}
