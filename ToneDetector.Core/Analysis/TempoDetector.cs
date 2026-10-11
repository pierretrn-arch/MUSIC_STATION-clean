namespace ToneDetector.Core.Analysis;

/// <summary>
/// Estimates tempo from the inter-onset intervals (IOIs) between consecutive note onsets:
/// bins IOIs at 10ms resolution, takes the dominant bin as the beat period, then normalizes
/// that period into the 60..180 BPM range (halving/doubling, since the dominant interval is
/// often a whole-note or half-note multiple of the true beat).
/// </summary>
public sealed class TempoDetector
{
    public const int MinIoisRequired = 6;
    private const double BinSizeMs = 10.0;

    private readonly List<double> _iois = new();
    private double? _lastOnsetMs;

    /// <summary>Records the start time (ms) of a newly committed note onset.</summary>
    public void AddNoteOnset(double onsetTimeMs)
    {
        if (_lastOnsetMs is double lastOnset)
            _iois.Add(onsetTimeMs - lastOnset);
        _lastOnsetMs = onsetTimeMs;
    }

    /// <summary>Returns the best tempo estimate, or null if fewer than <see cref="MinIoisRequired"/> IOIs have been seen.</summary>
    public DetectedTempo? Detect()
    {
        if (_iois.Count < MinIoisRequired)
            return null;

        var bins = new Dictionary<double, int>();
        foreach (double ioi in _iois)
        {
            double bin = Math.Round(ioi / BinSizeMs) * BinSizeMs;
            bins[bin] = bins.GetValueOrDefault(bin) + 1;
        }

        KeyValuePair<double, int> dominant = bins.OrderByDescending(kv => kv.Value).First();
        double beatPeriodMs = dominant.Key;
        int dominantCount = dominant.Value;

        while (beatPeriodMs > 0 && beatPeriodMs < 300.0)
            beatPeriodMs *= 2.0;
        while (beatPeriodMs > 1000.0)
            beatPeriodMs /= 2.0;

        double bpm = beatPeriodMs > 0 ? 60000.0 / beatPeriodMs : 0.0;
        double confidence = (double)dominantCount / _iois.Count;

        return new DetectedTempo(bpm, confidence);
    }

    public void Reset()
    {
        _iois.Clear();
        _lastOnsetMs = null;
    }
}
