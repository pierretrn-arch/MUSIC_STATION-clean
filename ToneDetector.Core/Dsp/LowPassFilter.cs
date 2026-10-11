namespace ToneDetector.Core.Dsp;

/// <summary>
/// User-configurable low-pass filter stage (e.g. to cut high-frequency noise/hiss before
/// pitch extraction). Backed by an RBJ-cookbook biquad section.
/// </summary>
public sealed class LowPassFilter : IAudioStage
{
    private readonly BiquadFilter _biquad;
    private int _sampleRate;
    private double _q;

    public bool Enabled { get; set; } = true;

    public double CutoffHz { get; private set; }

    public LowPassFilter(double cutoffHz, int sampleRate, double q = 0.7071)
    {
        CutoffHz = cutoffHz;
        _sampleRate = sampleRate;
        _q = q;
        _biquad = BiquadFilter.LowPass(cutoffHz, sampleRate, q);
    }

    /// <summary>Re-tunes the cutoff without losing the interface reference held by the pipeline.</summary>
    public void SetCutoff(double cutoffHz)
    {
        CutoffHz = cutoffHz;
        _biquad.ConfigureLowPass(cutoffHz, _sampleRate, _q);
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
