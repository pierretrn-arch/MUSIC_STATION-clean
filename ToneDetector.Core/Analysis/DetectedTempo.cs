namespace ToneDetector.Core.Analysis;

/// <summary>Result of tempo detection: estimated tempo in BPM (normalized into 60..180) and a
/// 0..1 confidence based on how dominant the winning inter-onset-interval bin was.</summary>
public readonly record struct DetectedTempo(double Bpm, double Confidence);
