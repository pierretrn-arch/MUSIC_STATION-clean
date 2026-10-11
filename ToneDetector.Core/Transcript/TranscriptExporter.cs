using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ToneDetector.Core.Pitch;

namespace ToneDetector.Core.Transcript;

/// <summary>
/// Serializes a <see cref="Transcript"/> (or, for the Piano Roll's own notes, a
/// <see cref="PianoRollNote"/> list) to interchange formats. JSON is full-fidelity and
/// re-importable; CSV is spreadsheet-friendly for quick inspection. MIDI/MusicXML export is a
/// documented future extension, not implemented here.
/// </summary>
public static class TranscriptExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Separate from <see cref="JsonOptions"/> deliberately - the Piano Roll JSON shape is
    /// new (no back-compat constraint), so this adds a string-enum converter for readability
    /// (<c>PianoRollNote.Origin</c> serializes as <c>"Drawn"</c>, not <c>1</c>) without touching the
    /// existing <see cref="Transcript"/>-based JSON format, which is unaffected by this class's
    /// Piano Roll additions.</summary>
    private static readonly JsonSerializerOptions RollNotesJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string ToJson(Transcript transcript) => JsonSerializer.Serialize(transcript, JsonOptions);

    public static void ExportJson(Transcript transcript, string filePath) =>
        File.WriteAllText(filePath, ToJson(transcript));

    public static string ToCsv(Transcript transcript)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Type,StartTimeMs,DurationMs,MidiNote,NoteName,CentsDeviation,MeanF0Hz,ConfidenceMean,ChordGroupId");

        foreach (TranscriptEvent evt in transcript.Events)
        {
            if (evt.Type == TranscriptEventType.Note && evt.Note is { } note)
            {
                builder.AppendLine(string.Join(',',
                    "Note",
                    evt.StartTimeMs.ToString("F1", CultureInfo.InvariantCulture),
                    evt.DurationMs.ToString("F1", CultureInfo.InvariantCulture),
                    note.MidiNumber,
                    note.NoteName,
                    note.CentsDeviation.ToString("F2", CultureInfo.InvariantCulture),
                    note.MeanF0Hz.ToString("F3", CultureInfo.InvariantCulture),
                    note.ConfidenceMean.ToString("F3", CultureInfo.InvariantCulture),
                    note.ChordGroupId?.ToString() ?? ""));
            }
            else
            {
                builder.AppendLine(string.Join(',',
                    "Silence",
                    evt.StartTimeMs.ToString("F1", CultureInfo.InvariantCulture),
                    evt.DurationMs.ToString("F1", CultureInfo.InvariantCulture),
                    "", "", "", "", "", ""));
            }
        }

        return builder.ToString();
    }

    public static void ExportCsv(Transcript transcript, string filePath) =>
        File.WriteAllText(filePath, ToCsv(transcript));

    /// <summary>Piano Roll variant of <see cref="ToJson(Transcript)"/>: the tab's tempo and time
    /// signature, then its notes sorted by start (<c>RollNotes</c> itself isn't guaranteed sorted
    /// after free edits/chord-transform). Each note has its start and length in beats, plus the same
    /// in ms at that tempo. Genuinely a different shape than the transcript's JSON: no
    /// <c>Silence</c> rows and no analysis-only fields (<c>CentsDeviation</c>/<c>MeanF0Hz</c>/
    /// <c>ConfidenceMean</c>) - those don't exist on a Piano Roll note, edited or not.</summary>
    public static string ToJson(IReadOnlyList<PianoRollNote> notes, double bpm)
    {
        var document = new RollJsonDocument(
            bpm,
            MusicalTime.TimeSignature,
            notes.OrderBy(n => n.StartBeat).Select(n => new RollJsonNote(
                n.Id, n.MidiNumber, NoteQuantizer.MidiNoteName(n.MidiNumber),
                n.StartBeat, n.DurationBeats,
                MusicalTime.BeatsToMs(n.StartBeat, bpm), MusicalTime.BeatsToMs(n.DurationBeats, bpm),
                n.Velocity, n.Origin, n.ChordGroupId)).ToList());
        return JsonSerializer.Serialize(document, RollNotesJsonOptions);
    }

    public static void ExportJson(IReadOnlyList<PianoRollNote> notes, string filePath, double bpm) =>
        File.WriteAllText(filePath, ToJson(notes, bpm));

    private sealed record RollJsonDocument(double TempoBpm, string TimeSignature, IReadOnlyList<RollJsonNote> Notes);

    private sealed record RollJsonNote(
        Guid Id, int MidiNumber, string NoteName, double StartBeat, double DurationBeats,
        double StartTimeMs, double DurationMs, int Velocity, PianoRollNoteOrigin Origin, Guid? ChordGroupId);

    /// <summary>Piano Roll variant of <see cref="ToCsv(Transcript)"/> - same spreadsheet-friendly
    /// intent, different columns: no <c>Type</c> (every row is a note) or analysis-only columns,
    /// plus <c>Velocity</c>/<c>Origin</c> which only exist on <see cref="PianoRollNote"/>. Times in
    /// ms are worked out at the tab's tempo; the start and length in beats are the last two columns.</summary>
    public static string ToCsv(IReadOnlyList<PianoRollNote> notes, double bpm)
    {
        var builder = new StringBuilder();
        builder.AppendLine("MidiNote,NoteName,StartTimeMs,DurationMs,Velocity,Origin,ChordGroupId,StartBeat,DurationBeats");

        foreach (PianoRollNote note in notes.OrderBy(n => n.StartBeat))
        {
            builder.AppendLine(string.Join(',',
                note.MidiNumber,
                NoteQuantizer.MidiNoteName(note.MidiNumber),
                MusicalTime.BeatsToMs(note.StartBeat, bpm).ToString("F1", CultureInfo.InvariantCulture),
                MusicalTime.BeatsToMs(note.DurationBeats, bpm).ToString("F1", CultureInfo.InvariantCulture),
                note.Velocity,
                note.Origin,
                note.ChordGroupId?.ToString() ?? "",
                note.StartBeat.ToString("0.####", CultureInfo.InvariantCulture),
                note.DurationBeats.ToString("0.####", CultureInfo.InvariantCulture)));
        }

        return builder.ToString();
    }

    public static void ExportCsv(IReadOnlyList<PianoRollNote> notes, string filePath, double bpm) =>
        File.WriteAllText(filePath, ToCsv(notes, bpm));

    /// <summary>Several tracks: the tempo and time signature, then each track with its name,
    /// colour, instrument, mute/solo, volume (dB, null when silent), pan and notes (same note
    /// fields as the single-track export).</summary>
    public static string ToJson(IReadOnlyList<TrackExport> tracks, double bpm)
    {
        var document = new TracksJsonDocument(
            bpm,
            MusicalTime.TimeSignature,
            tracks.Select(t => new TrackJson(
                t.Name, t.Colour, t.Instrument, t.IsMuted, t.IsSolo,
                double.IsNegativeInfinity(MixerMath.PositionToDb(t.VolumePosition)) ? null : Math.Round(MixerMath.PositionToDb(t.VolumePosition), 2),
                t.Pan,
                t.Notes.OrderBy(n => n.StartBeat).Select(n => ToJsonNote(n, bpm)).ToList())).ToList());
        return JsonSerializer.Serialize(document, RollNotesJsonOptions);
    }

    public static void ExportJson(IReadOnlyList<TrackExport> tracks, string filePath, double bpm) =>
        File.WriteAllText(filePath, ToJson(tracks, bpm));

    /// <summary>Several tracks in one table: a Track column first, then the single-track columns;
    /// rows go track by track, each in time order.</summary>
    public static string ToCsv(IReadOnlyList<TrackExport> tracks, double bpm)
    {
        var builder = new StringBuilder();
        builder.AppendLine("Track,MidiNote,NoteName,StartTimeMs,DurationMs,Velocity,Origin,ChordGroupId,StartBeat,DurationBeats");
        foreach (TrackExport track in tracks)
        {
            foreach (PianoRollNote note in track.Notes.OrderBy(n => n.StartBeat))
                builder.AppendLine(CsvField(track.Name) + "," + CsvRow(note, bpm));
        }
        return builder.ToString();
    }

    public static void ExportCsv(IReadOnlyList<TrackExport> tracks, string filePath, double bpm) =>
        File.WriteAllText(filePath, ToCsv(tracks, bpm));

    private static RollJsonNote ToJsonNote(PianoRollNote n, double bpm) => new(
        n.Id, n.MidiNumber, NoteQuantizer.MidiNoteName(n.MidiNumber),
        n.StartBeat, n.DurationBeats,
        MusicalTime.BeatsToMs(n.StartBeat, bpm), MusicalTime.BeatsToMs(n.DurationBeats, bpm),
        n.Velocity, n.Origin, n.ChordGroupId);

    private static string CsvRow(PianoRollNote note, double bpm) => string.Join(',',
        note.MidiNumber,
        NoteQuantizer.MidiNoteName(note.MidiNumber),
        MusicalTime.BeatsToMs(note.StartBeat, bpm).ToString("F1", CultureInfo.InvariantCulture),
        MusicalTime.BeatsToMs(note.DurationBeats, bpm).ToString("F1", CultureInfo.InvariantCulture),
        note.Velocity,
        note.Origin,
        note.ChordGroupId?.ToString() ?? "",
        note.StartBeat.ToString("0.####", CultureInfo.InvariantCulture),
        note.DurationBeats.ToString("0.####", CultureInfo.InvariantCulture));

    /// <summary>A track name as a CSV field: quoted when it holds a comma, quote or line break.</summary>
    private static string CsvField(string text) =>
        text.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    private sealed record TracksJsonDocument(double TempoBpm, string TimeSignature, IReadOnlyList<TrackJson> Tracks);

    private sealed record TrackJson(
        string Name, string Colour, string Instrument, bool Muted, bool Solo, double? VolumeDb, int Pan,
        IReadOnlyList<RollJsonNote> Notes);
}
