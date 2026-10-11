namespace ToneDetector.Core.Transcript;

public sealed record TranscriptEvent(
    TranscriptEventType Type,
    double StartTimeMs,
    double DurationMs,
    NoteInfo? Note);
