namespace ToneDetector.Core.Pipeline;

/// <summary>
/// Result of running one audio frame through the full analysis pipeline: raw detector output
/// plus the smoothed pitch track quantized to the nearest note.
/// </summary>
public readonly record struct PitchAnalysisResult(
    double RawF0Hz,
    double SmoothedF0Hz,
    int MidiNote,
    double CentsDeviation,
    bool IsVoiced,
    double RmsEnergy,
    double Aperiodicity);
