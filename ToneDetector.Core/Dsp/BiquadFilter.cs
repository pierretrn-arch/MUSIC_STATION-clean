namespace ToneDetector.Core.Dsp;

/// <summary>
/// A single second-order IIR section (Direct Form I), configured from the RBJ Audio EQ
/// Cookbook formulas. Used as the building block for the low-pass/high-pass audio stages.
/// </summary>
public sealed class BiquadFilter
{
    private double _b0, _b1, _b2, _a1, _a2;
    private double _x1, _x2, _y1, _y2;

    public static BiquadFilter LowPass(double cutoffHz, int sampleRate, double q = 0.7071)
    {
        var filter = new BiquadFilter();
        filter.ConfigureLowPass(cutoffHz, sampleRate, q);
        return filter;
    }

    public static BiquadFilter HighPass(double cutoffHz, int sampleRate, double q = 0.7071)
    {
        var filter = new BiquadFilter();
        filter.ConfigureHighPass(cutoffHz, sampleRate, q);
        return filter;
    }

    public void ConfigureLowPass(double cutoffHz, int sampleRate, double q)
    {
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * q);

        double a0 = 1 + alpha;
        double b0 = (1 - cosW0) / 2.0;
        double b1 = 1 - cosW0;
        double b2 = (1 - cosW0) / 2.0;
        double a1 = -2.0 * cosW0;
        double a2 = 1 - alpha;

        SetNormalizedCoefficients(a0, a1, a2, b0, b1, b2);
    }

    public void ConfigureHighPass(double cutoffHz, int sampleRate, double q)
    {
        double w0 = 2.0 * Math.PI * cutoffHz / sampleRate;
        double cosW0 = Math.Cos(w0);
        double alpha = Math.Sin(w0) / (2.0 * q);

        double a0 = 1 + alpha;
        double b0 = (1 + cosW0) / 2.0;
        double b1 = -(1 + cosW0);
        double b2 = (1 + cosW0) / 2.0;
        double a1 = -2.0 * cosW0;
        double a2 = 1 - alpha;

        SetNormalizedCoefficients(a0, a1, a2, b0, b1, b2);
    }

    private void SetNormalizedCoefficients(double a0, double a1, double a2, double b0, double b1, double b2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public float ProcessSample(float x0)
    {
        double y0 = _b0 * x0 + _b1 * _x1 + _b2 * _x2 - _a1 * _y1 - _a2 * _y2;
        _x2 = _x1;
        _x1 = x0;
        _y2 = _y1;
        _y1 = y0;
        return (float)y0;
    }

    public void Reset()
    {
        _x1 = _x2 = _y1 = _y2 = 0.0;
    }
}
