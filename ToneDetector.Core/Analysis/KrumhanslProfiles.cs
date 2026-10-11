namespace ToneDetector.Core.Analysis;

/// <summary>
/// Krumhansl-Schmuckler key-profile weights: how strongly each scale degree (relative to the
/// tonic, index 0) is expected to appear in a passage written in that key/mode. Derived from
/// music-perception probe-tone experiments (Krumhansl &amp; Kessler, 1982).
/// </summary>
public static class KrumhanslProfiles
{
    public static readonly double[] Major =
    {
        6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88,
    };

    public static readonly double[] Minor =
    {
        6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17,
    };
}
