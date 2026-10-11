namespace ToneDetector.Core.Analysis;

/// <summary>
/// Measures vibrato from a completed note's per-frame cents-deviation series: removes the
/// mean (the note's center pitch) and counts zero crossings of the remainder to estimate
/// oscillation rate, and takes the half peak-to-peak range as the extent.
/// </summary>
public static class VibratoAnalyzer
{
    private const double MinNoteDurationMs = 400.0;
    private const int MinZeroCrossings = 3;

    public static VibratoAnalysis? Analyze(IReadOnlyList<double> centsDeviationSeries, double durationMs)
    {
        if (durationMs < MinNoteDurationMs || centsDeviationSeries.Count < 2)
            return null;

        double mean = centsDeviationSeries.Average();
        double[] centered = centsDeviationSeries.Select(c => c - mean).ToArray();

        int zeroCrossings = 0;
        for (int i = 0; i < centered.Length - 1; i++)
        {
            if (centered[i] * centered[i + 1] < 0)
                zeroCrossings++;
        }

        if (zeroCrossings < MinZeroCrossings)
            return null;

        double durationSeconds = durationMs / 1000.0;
        double rateHz = (zeroCrossings / 2.0) / durationSeconds;
        double extentCents = (centered.Max() - centered.Min()) / 2.0;

        return new VibratoAnalysis(rateHz, extentCents);
    }
}
