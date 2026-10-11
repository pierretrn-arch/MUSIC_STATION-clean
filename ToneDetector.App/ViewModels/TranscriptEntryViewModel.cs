using ToneDetector.Core.Pitch;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>Row model for the live transcript grid (separate from File Analysis's <see cref="TranscriptEventRow"/>
/// because the live grid additionally shows Octave and the gap since the previous row).</summary>
public sealed class TranscriptEntryViewModel
{
    public string Type { get; init; } = "";
    public string Note { get; init; } = "";
    public int Octave { get; init; }
    public int MidiNumber { get; init; }
    public double StartTimeMs { get; init; }
    public double DurationMs { get; init; }
    public double GapSincePrevMs { get; init; }
    public double CentsDeviation { get; init; }
    public double ConfidenceMean { get; init; }
    public double MeanF0Hz { get; init; }
    public Guid? ChordGroupId { get; init; }

    /// <summary>Compact grid indicator for chord-voice rows; empty for ordinary notes.</summary>
    public string ChordBadge => ChordGroupId is null ? "" : "♪";

    public static TranscriptEntryViewModel FromEvent(TranscriptEvent evt, double gapSincePrevMs)
    {
        if (evt.Type == TranscriptEventType.Note && evt.Note is { } note)
        {
            (string name, int octave) = NoteQuantizer.GetNoteNameAndOctave(note.MidiNumber);
            return new TranscriptEntryViewModel
            {
                Type = "Note",
                Note = name,
                Octave = octave,
                MidiNumber = note.MidiNumber,
                StartTimeMs = evt.StartTimeMs,
                DurationMs = evt.DurationMs,
                GapSincePrevMs = gapSincePrevMs,
                CentsDeviation = note.CentsDeviation,
                ConfidenceMean = note.ConfidenceMean,
                MeanF0Hz = note.MeanF0Hz,
                ChordGroupId = note.ChordGroupId,
            };
        }

        return new TranscriptEntryViewModel
        {
            Type = "Silence",
            StartTimeMs = evt.StartTimeMs,
            DurationMs = evt.DurationMs,
            GapSincePrevMs = gapSincePrevMs,
        };
    }
}
