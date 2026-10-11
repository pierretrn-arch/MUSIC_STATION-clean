namespace ToneDetector.Core.Analysis;

/// <summary>Vibrato characteristics measured over a single sustained note: oscillation rate
/// (Hz) and half-amplitude extent (cents) of the pitch's wobble around its center.</summary>
public readonly record struct VibratoAnalysis(double RateHz, double ExtentCents);
