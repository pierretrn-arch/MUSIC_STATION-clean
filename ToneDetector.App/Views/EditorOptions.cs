using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Views;

/// <summary>Values shared by the Live Tuner and File Analysis editor tool bars.</summary>
public static class EditorOptions
{
    /// <summary>Chord qualities in <see cref="ChordQuality"/> order ("Major", "Minor", …); the
    /// Apply handler parses the selected name back with Enum.Parse.</summary>
    public static IReadOnlyList<string> ChordQualities { get; } = Enum.GetNames<ChordQuality>();
}
