using System.Globalization;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>
/// One row of the File Analysis results panel (Design/FileAnalysis.dc.html): Note, Start (ms),
/// Duration (ms) and a confidence bar, plus Type / Cents / Mean F0 tiles when the row is expanded.
/// Numbers use a decimal point and a real minus sign; missing values show "—".
/// </summary>
public sealed class AnalysisResultRow
{
    private const string NoValue = "—";

    public string TypeText { get; init; } = "";
    public string NoteText { get; init; } = NoValue;
    public string StartText { get; init; } = "";
    public string DurationText { get; init; } = "";

    /// <summary>0..1, or null for a Silence row (no bar).</summary>
    public double? Confidence { get; init; }
    public string ConfidenceText { get; init; } = NoValue;
    public string CentsText { get; init; } = NoValue;
    public string MeanF0Text { get; init; } = NoValue;

    /// <summary>Confidence bar fill width inside its 48px track.</summary>
    public double ConfidenceBarWidth => Confidence is double c ? Math.Clamp(c, 0, 1) * 48.0 : 0.0;

    /// <summary>Rows for a track's notes in time order (then pitch), with a Silence row for each
    /// gap where nothing sounds (unless <paramref name="hideSilence"/>). Times in ms at <paramref name="bpm"/> from bar 1. Notes without
    /// detection data (drawn, split, MIDI, or moved to another pitch) show "—" for Confidence,
    /// Cents and Mean F0.</summary>
    public static IReadOnlyList<AnalysisResultRow> Build(IEnumerable<PianoRollNote> notes, double bpm, bool hideSilence = true)
    {
        var rows = new List<AnalysisResultRow>();
        double coveredUntilMs = 0.0;
        foreach (PianoRollNote note in notes.OrderBy(n => n.StartBeat).ThenBy(n => n.MidiNumber))
        {
            double startMs = MusicalTime.BeatsToMs(note.StartBeat, bpm);
            double durationMs = MusicalTime.BeatsToMs(note.DurationBeats, bpm);
            if (!hideSilence && startMs - coveredUntilMs >= 1.0)
                rows.Add(new AnalysisResultRow { TypeText = "Silence", StartText = Ms(coveredUntilMs), DurationText = Ms(startMs - coveredUntilMs) });
            rows.Add(FromNote(note, startMs, durationMs));
            coveredUntilMs = Math.Max(coveredUntilMs, startMs + durationMs);
        }
        return rows;
    }

    private static AnalysisResultRow FromNote(PianoRollNote note, double startMs, double durationMs) => new()
    {
        TypeText = "Note",
        NoteText = ToneDetector.Core.Pitch.NoteQuantizer.MidiNoteName(note.MidiNumber),
        StartText = Ms(startMs),
        DurationText = Ms(durationMs),
        Confidence = note.Confidence,
        ConfidenceText = note.Confidence is double c ? c.ToString("0.00", CultureInfo.InvariantCulture) : NoValue,
        CentsText = note.CentsDeviation is double cents ? LiveTunerViewModel.FormatSignedCents(cents) : NoValue,
        MeanF0Text = note.MeanF0Hz is double f0 ? f0.ToString("0.00", CultureInfo.InvariantCulture) : NoValue,
    };

    private static string Ms(double ms) => Math.Round(ms).ToString("0", CultureInfo.InvariantCulture);

    public static AnalysisResultRow FromEvent(TranscriptEvent evt)
    {
        string start = Math.Round(evt.StartTimeMs).ToString("0", CultureInfo.InvariantCulture);
        string duration = Math.Round(evt.DurationMs).ToString("0", CultureInfo.InvariantCulture);

        if (evt.Type == TranscriptEventType.Note && evt.Note is { } note)
        {
            return new AnalysisResultRow
            {
                TypeText = "Note",
                NoteText = note.NoteName,
                StartText = start,
                DurationText = duration,
                Confidence = note.ConfidenceMean,
                ConfidenceText = note.ConfidenceMean.ToString("0.00", CultureInfo.InvariantCulture),
                CentsText = LiveTunerViewModel.FormatSignedCents(note.CentsDeviation),
                MeanF0Text = note.MeanF0Hz.ToString("0.00", CultureInfo.InvariantCulture),
            };
        }

        return new AnalysisResultRow
        {
            TypeText = "Silence",
            StartText = start,
            DurationText = duration,
        };
    }
}
