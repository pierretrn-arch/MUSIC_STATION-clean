namespace ToneDetector.Core.Transcript;

/// <summary>How a <see cref="PianoRollNote"/> came to exist - reserved for later phases (export-source
/// selection, etc.); not consumed by anything yet.</summary>
public enum PianoRollNoteOrigin
{
    SeededFromTranscript,
    Drawn,
    Edited,
}

/// <summary>A single note in the Piano Roll's independent, free-form polyphonic note layer -
/// decoupled from <see cref="TranscriptEvent"/> so notes can overlap at any pitch/time without the
/// neighbor-clamping the transcript's strictly time-ordered model requires. Times are in beats
/// (quarter notes, see <see cref="MusicalTime"/>), so a tempo change keeps notes on their beats.
/// <see cref="Id"/> is the note's stable identity across edits: an edit produces a new
/// <c>PianoRollNote</c> instance via <c>with</c>, but must never override <c>Id</c> - callers
/// (hit-testing, undo, dictionary-keying) rely on it staying constant for the note's whole lifetime.
/// <see cref="Confidence"/>, <see cref="CentsDeviation"/> and <see cref="MeanF0Hz"/> come from pitch detection and stay with
/// a detected note through moves and resizes; they're null for drawn notes, split halves, and any
/// note whose pitch was changed (the detection no longer applies). <see cref="ChordName"/> is set
/// on chord voices ("C Major").</summary>
public sealed record PianoRollNote(
    Guid Id,
    int MidiNumber,
    double StartBeat,
    double DurationBeats,
    int Velocity = 100,
    PianoRollNoteOrigin Origin = PianoRollNoteOrigin.SeededFromTranscript,
    Guid? ChordGroupId = null,
    double? Confidence = null,
    double? CentsDeviation = null,
    string? ChordName = null,
    double? MeanF0Hz = null)
{
    public double EndBeat => StartBeat + DurationBeats;
}
