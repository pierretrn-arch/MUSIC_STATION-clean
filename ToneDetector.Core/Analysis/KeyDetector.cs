namespace ToneDetector.Core.Analysis;

/// <summary>
/// Krumhansl-Schmuckler key/scale detector: accumulates a duration-weighted pitch-class
/// histogram from a stream of notes and, once enough notes have been seen, correlates it
/// against all 24 major/minor key profiles to find the best match.
/// </summary>
public sealed class KeyDetector
{
    public const int MinNotesRequired = 8;

    private readonly double[] _histogram = new double[12];
    private int _noteCount;

    /// <summary>Adds one note's contribution to the pitch-class histogram, weighted by its duration.</summary>
    public void AddNote(int pitchClass, double durationSeconds)
    {
        _histogram[((pitchClass % 12) + 12) % 12] += durationSeconds;
        _noteCount++;
    }

    /// <summary>Returns the best-matching key, or null if fewer than <see cref="MinNotesRequired"/> notes have been added.</summary>
    public DetectedKey? Detect()
    {
        if (_noteCount < MinNotesRequired)
            return null;

        double bestCorrelation = double.NegativeInfinity;
        int bestTonic = 0;
        MusicalMode bestMode = MusicalMode.Major;

        for (int tonic = 0; tonic < 12; tonic++)
        {
            foreach (MusicalMode mode in new[] { MusicalMode.Major, MusicalMode.Minor })
            {
                double[] profile = mode == MusicalMode.Major ? KrumhanslProfiles.Major : KrumhanslProfiles.Minor;
                double correlation = PearsonCorrelation(_histogram, RotateProfile(profile, tonic));
                if (correlation > bestCorrelation)
                {
                    bestCorrelation = correlation;
                    bestTonic = tonic;
                    bestMode = mode;
                }
            }
        }

        return new DetectedKey(bestTonic, bestMode, Math.Max(0.0, bestCorrelation));
    }

    public void Reset()
    {
        Array.Clear(_histogram);
        _noteCount = 0;
    }

    /// <summary>Aligns the profile's tonic (index 0) with the given pitch class.</summary>
    private static double[] RotateProfile(double[] profile, int tonic)
    {
        var rotated = new double[12];
        for (int i = 0; i < 12; i++)
            rotated[(tonic + i) % 12] = profile[i];
        return rotated;
    }

    private static double PearsonCorrelation(double[] x, double[] y)
    {
        double meanX = x.Average();
        double meanY = y.Average();

        double numerator = 0.0, sumSqX = 0.0, sumSqY = 0.0;
        for (int i = 0; i < x.Length; i++)
        {
            double dx = x[i] - meanX;
            double dy = y[i] - meanY;
            numerator += dx * dy;
            sumSqX += dx * dx;
            sumSqY += dy * dy;
        }

        double denominator = Math.Sqrt(sumSqX * sumSqY);
        return denominator < 1e-12 ? 0.0 : numerator / denominator;
    }
}
