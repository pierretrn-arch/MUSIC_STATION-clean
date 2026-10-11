using ToneDetector.Core.Analysis;
using ToneDetector.Core.Pitch;

namespace ToneDetector.Core.Transcript;

/// <summary>Pitch details for a Note transcript event, averaged over its duration. Vibrato is
/// null when the note was too short or too steady to measure (see <see cref="VibratoAnalyzer"/>).
/// <c>ChordGroupId</c> is null for an ordinary detected note; when a chord transform (see
/// <see cref="ChordBuilder"/>) replaces one note with several voices, all voices share the same
/// freshly-generated id so they can be identified as one chord event later.</summary>
public readonly record struct NoteInfo(
    int MidiNumber,
    double CentsDeviation,
    double MeanF0Hz,
    double ConfidenceMean,
    VibratoAnalysis? Vibrato = null,
    Guid? ChordGroupId = null)
{
    public string NoteName => NoteQuantizer.MidiNoteName(MidiNumber);
}
