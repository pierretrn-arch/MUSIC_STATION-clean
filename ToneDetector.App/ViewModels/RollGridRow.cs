using System.Globalization;
using ToneDetector.Core.Pitch;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>
/// One row of the Live Tuner side panel's Grid (Design/LiveTuner.dc.html): Note (E4), Start (ms),
/// Duration (ms) and Confidence (bar + number) for a piano-roll note, or a Silence row for a gap
/// where nothing sounds. The clicked row expands into Type, Gap since prev (ms), Cents and Chord.
/// Times are the notes' beats at the tab's tempo, from bar 1. Missing values show "—".
/// </summary>
public sealed class RollGridRow
{
    private const string NoValue = "—";
    private const double MinSilenceMs = 1.0;

    /// <summary>The note's id, or <see cref="Guid.Empty"/> for a Silence row.</summary>
    public Guid NoteId { get; init; }
    public bool IsSilence { get; init; }
    public string TypeText { get; init; } = "Note";
    public string NoteText { get; init; } = NoValue;
    public string StartText { get; init; } = "";
    public string DurationText { get; init; } = "";

    /// <summary>0..1, or null (Silence, or a drawn/edited note with no detection).</summary>
    public double? Confidence { get; init; }
    public string ConfidenceText { get; init; } = NoValue;
    public string GapText { get; init; } = NoValue;
    public string CentsText { get; init; } = NoValue;
    public string ChordText { get; init; } = NoValue;

    /// <summary>Confidence bar fill width inside its 44px track.</summary>
    public double ConfidenceBarWidth => Confidence is double c ? Math.Clamp(c, 0, 1) * 44.0 : 0.0;

    /// <summary>Rows in time order (then pitch, low to high), with a Silence row for each gap
    /// between the end of everything before and the next note (including before the first note),
    /// unless <paramref name="hideSilence"/>.</summary>
    public static IReadOnlyList<RollGridRow> Build(IEnumerable<PianoRollNote> notes, double bpm, bool hideSilence = true)
    {
        var rows = new List<RollGridRow>();
        double coveredUntilMs = 0.0;
        bool first = true;

        foreach (PianoRollNote note in notes.OrderBy(n => n.StartBeat).ThenBy(n => n.MidiNumber))
        {
            double startMs = MusicalTime.BeatsToMs(note.StartBeat, bpm);
            double durationMs = MusicalTime.BeatsToMs(note.DurationBeats, bpm);
            double gapMs = startMs - coveredUntilMs;

            if (!hideSilence && gapMs >= MinSilenceMs)
            {
                rows.Add(new RollGridRow
                {
                    NoteId = Guid.Empty,
                    IsSilence = true,
                    TypeText = "Silence",
                    StartText = Ms(coveredUntilMs),
                    DurationText = Ms(gapMs),
                });
            }

            rows.Add(new RollGridRow
            {
                NoteId = note.Id,
                NoteText = NoteQuantizer.MidiNoteName(note.MidiNumber),
                StartText = Ms(startMs),
                DurationText = Ms(durationMs),
                Confidence = note.Confidence,
                ConfidenceText = note.Confidence is double c ? c.ToString("0.00", CultureInfo.InvariantCulture) : NoValue,
                GapText = first ? NoValue : Ms(Math.Max(0.0, gapMs)),
                CentsText = note.CentsDeviation is double cents ? LiveTunerViewModel.FormatSignedCents(cents) : NoValue,
                ChordText = note.ChordName ?? NoValue,
            });

            coveredUntilMs = Math.Max(coveredUntilMs, startMs + durationMs);
            first = false;
        }

        return rows;
    }

    private static string Ms(double ms) => Math.Round(ms).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>"[track] · 12 notes" caption count.</summary>
    public static string CountText(int count) => count == 1 ? "1 note" : $"{count} notes";
}
