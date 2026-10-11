using ToneDetector.Core.Pitch;

namespace ToneDetector.Core.Analysis;

public enum MusicalMode
{
    Major,
    Minor,
}

/// <summary>Result of key/scale detection: the tonic pitch class (0=C .. 11=B), its mode, and
/// a 0..1 confidence (the winning profile's correlation with the observed pitch-class weights).</summary>
public readonly record struct DetectedKey(int Tonic, MusicalMode Mode, double Confidence)
{
    public string TonicName => NoteQuantizer.PitchClassName(Tonic);

    public string DisplayName => $"{TonicName} {(Mode == MusicalMode.Major ? "major" : "minor")}";
}
