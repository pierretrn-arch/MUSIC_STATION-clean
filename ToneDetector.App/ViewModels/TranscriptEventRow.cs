using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>Flattened, display-friendly projection of a <see cref="TranscriptEvent"/> for grid binding.</summary>
public sealed class TranscriptEventRow
{
    public string Type { get; init; } = "";
    public double StartTimeMs { get; init; }
    public double DurationMs { get; init; }
    public string NoteName { get; init; } = "";
    public double CentsDeviation { get; init; }
    public double MeanF0Hz { get; init; }
    public double ConfidenceMean { get; init; }

    public static TranscriptEventRow FromEvent(TranscriptEvent evt)
    {
        if (evt.Type == TranscriptEventType.Note && evt.Note is { } note)
        {
            return new TranscriptEventRow
            {
                Type = "Note",
                StartTimeMs = evt.StartTimeMs,
                DurationMs = evt.DurationMs,
                NoteName = note.NoteName,
                CentsDeviation = note.CentsDeviation,
                MeanF0Hz = note.MeanF0Hz,
                ConfidenceMean = note.ConfidenceMean,
            };
        }

        return new TranscriptEventRow
        {
            Type = "Silence",
            StartTimeMs = evt.StartTimeMs,
            DurationMs = evt.DurationMs,
        };
    }
}
