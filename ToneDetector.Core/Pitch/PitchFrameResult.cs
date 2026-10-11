namespace ToneDetector.Core.Pitch;

/// <summary>
/// Result of running the pitch detector over a single analysis frame.
/// </summary>
public readonly record struct PitchFrameResult(
    double F0Hz,
    double Aperiodicity,
    bool IsVoiced,
    double RmsEnergy)
{
    public static PitchFrameResult Unvoiced(double rmsEnergy) => new(0.0, 1.0, false, rmsEnergy);
}
