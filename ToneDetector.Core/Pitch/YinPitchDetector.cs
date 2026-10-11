namespace ToneDetector.Core.Pitch;

/// <summary>
/// YIN fundamental-frequency estimator (de Cheveigne &amp; Kawahara, 2002), with parabolic
/// interpolation around the chosen cumulative mean normalized difference function (CMNDF)
/// minimum to recover sub-sample lag precision (this is what gets accuracy to sub-cent level
/// on sustained tones, rather than being limited to one sample-period of resolution).
/// </summary>
public sealed class YinPitchDetector : IPitchDetector
{
    /// <summary>Lowest fundamental this detector will report (Hz). Bounds the search window.</summary>
    public double MinFrequencyHz { get; init; } = 50.0;

    /// <summary>Highest fundamental this detector will report (Hz).</summary>
    public double MaxFrequencyHz { get; init; } = 1500.0;

    /// <summary>CMNDF value below which a dip is accepted as periodic (YIN's "threshold" parameter).</summary>
    public double AperiodicityThreshold { get; init; } = 0.15;

    /// <summary>RMS below this is treated as silence without running the pitch search at all.</summary>
    public double RmsSilenceThreshold { get; init; } = 0.01;

    public PitchFrameResult DetectPitch(ReadOnlySpan<float> frame, int sampleRate)
    {
        double rms = ComputeRms(frame);
        if (rms < RmsSilenceThreshold)
            return PitchFrameResult.Unvoiced(rms);

        // Classic YIN uses a fixed integration window W and searches lags tau in [0, W],
        // requiring a buffer of at least 2*W samples so frame[j+tau] stays in range.
        int window = frame.Length / 2;
        int maxTau = Math.Min((int)(sampleRate / MinFrequencyHz), window - 1);
        int minTau = Math.Max(1, (int)(sampleRate / MaxFrequencyHz));
        if (maxTau <= minTau)
            return PitchFrameResult.Unvoiced(rms);

        Span<double> cmndf = maxTau + 1 <= 4096
            ? stackalloc double[maxTau + 1]
            : new double[maxTau + 1];
        ComputeCumulativeMeanNormalizedDifference(frame, window, maxTau, cmndf);

        int tau = FindFirstDipBelowThreshold(cmndf, minTau, maxTau, AperiodicityThreshold);
        bool voiced = tau >= 0;
        if (!voiced)
        {
            // No dip cleared the threshold: fall back to the global minimum in range so we can
            // still report a best-effort pitch, but flag it unvoiced via the aperiodicity value.
            tau = ArgMin(cmndf, minTau, maxTau);
        }

        double refinedTau = ParabolicInterpolate(cmndf, tau);
        double f0 = sampleRate / refinedTau;
        double aperiodicity = cmndf[tau];

        bool isVoiced = voiced && aperiodicity < AperiodicityThreshold;
        return new PitchFrameResult(f0, aperiodicity, isVoiced, rms);
    }

    private static double ComputeRms(ReadOnlySpan<float> frame)
    {
        double sumSquares = 0.0;
        for (int i = 0; i < frame.Length; i++)
            sumSquares += (double)frame[i] * frame[i];
        return Math.Sqrt(sumSquares / frame.Length);
    }

    /// <summary>
    /// Computes YIN's cumulative mean normalized difference function for tau = 0..maxTau.
    /// cmndf[0] is defined as 1 by convention (never selected as the pitch period).
    /// </summary>
    private static void ComputeCumulativeMeanNormalizedDifference(
        ReadOnlySpan<float> frame, int window, int maxTau, Span<double> cmndf)
    {
        cmndf[0] = 1.0;
        double runningSum = 0.0;

        Span<double> diff = maxTau + 1 <= 4096 ? stackalloc double[maxTau + 1] : new double[maxTau + 1];
        for (int tau = 1; tau <= maxTau; tau++)
        {
            double sum = 0.0;
            for (int j = 0; j < window; j++)
            {
                double delta = frame[j] - frame[j + tau];
                sum += delta * delta;
            }
            diff[tau] = sum;
        }

        for (int tau = 1; tau <= maxTau; tau++)
        {
            runningSum += diff[tau];
            cmndf[tau] = runningSum == 0.0 ? 1.0 : diff[tau] * tau / runningSum;
        }
    }

    /// <summary>
    /// Scans tau from minTau upward for the first local minimum whose CMNDF value drops below
    /// threshold, per the original YIN "absolute threshold" step. Returns -1 if none qualifies.
    /// </summary>
    private static int FindFirstDipBelowThreshold(ReadOnlySpan<double> cmndf, int minTau, int maxTau, double threshold)
    {
        for (int tau = minTau; tau <= maxTau; tau++)
        {
            if (cmndf[tau] >= threshold)
                continue;

            // Descend to the local minimum of this dip.
            while (tau + 1 <= maxTau && cmndf[tau + 1] < cmndf[tau])
                tau++;

            return tau;
        }
        return -1;
    }

    private static int ArgMin(ReadOnlySpan<double> cmndf, int minTau, int maxTau)
    {
        int best = minTau;
        for (int tau = minTau + 1; tau <= maxTau; tau++)
        {
            if (cmndf[tau] < cmndf[best])
                best = tau;
        }
        return best;
    }

    /// <summary>
    /// Refines an integer lag to sub-sample precision by fitting a parabola through the CMNDF
    /// values at tau-1, tau, tau+1 and returning the vertex position.
    /// </summary>
    private static double ParabolicInterpolate(ReadOnlySpan<double> cmndf, int tau)
    {
        if (tau <= 0 || tau >= cmndf.Length - 1)
            return tau;

        double y0 = cmndf[tau - 1];
        double y1 = cmndf[tau];
        double y2 = cmndf[tau + 1];

        double denominator = 2 * (2 * y1 - y0 - y2);
        if (Math.Abs(denominator) < 1e-12)
            return tau;

        double offset = (y2 - y0) / denominator;
        return tau + offset;
    }
}
