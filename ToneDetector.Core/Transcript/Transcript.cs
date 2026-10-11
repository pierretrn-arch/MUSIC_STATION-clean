namespace ToneDetector.Core.Transcript;

public sealed record Transcript(
    string SourceInfo,
    IReadOnlyList<TranscriptEvent> Events);
