using System.Text.Json;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Covers the <see cref="PianoRollNote"/>-based overloads - both tabs' Export JSON/CSV
/// use these, so exported files reflect piano roll edits, with the tab's tempo and each note's
/// beats. The original <see cref="Transcript"/>-based overloads had no prior test coverage.</summary>
public class TranscriptExporterTests
{
    [Fact]
    public void ToJson_FromPianoRollNotes_RoundTripsAllFields()
    {
        Guid chordGroupId = Guid.NewGuid();
        var notes = new[]
        {
            new PianoRollNote(Guid.NewGuid(), 64, 1, 0.5, Velocity: 90, Origin: PianoRollNoteOrigin.Edited, ChordGroupId: chordGroupId),
            new PianoRollNote(Guid.NewGuid(), 60, 0, 1, Velocity: 110, Origin: PianoRollNoteOrigin.Drawn),
        };

        string json = TranscriptExporter.ToJson(notes, bpm: 120);
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(120.0, doc.RootElement.GetProperty("TempoBpm").GetDouble());
        Assert.Equal("4/4", doc.RootElement.GetProperty("TimeSignature").GetString());
        JsonElement[] elements = doc.RootElement.GetProperty("Notes").EnumerateArray().ToArray();

        // Sorted by start on output, regardless of input order; ms worked out at 120 BPM.
        Assert.Equal(2, elements.Length);
        Assert.Equal(60, elements[0].GetProperty("MidiNumber").GetInt32());
        Assert.Equal(0.0, elements[0].GetProperty("StartBeat").GetDouble());
        Assert.Equal(1.0, elements[0].GetProperty("DurationBeats").GetDouble());
        Assert.Equal(0.0, elements[0].GetProperty("StartTimeMs").GetDouble());
        Assert.Equal(500.0, elements[0].GetProperty("DurationMs").GetDouble());
        Assert.Equal(500.0, elements[1].GetProperty("StartTimeMs").GetDouble());
        Assert.Equal(110, elements[0].GetProperty("Velocity").GetInt32());
        Assert.Equal("Drawn", elements[0].GetProperty("Origin").GetString()); // string enum, not an int
        Assert.False(elements[0].TryGetProperty("ChordGroupId", out JsonElement chordProp) && chordProp.ValueKind != JsonValueKind.Null);

        Assert.Equal(64, elements[1].GetProperty("MidiNumber").GetInt32());
        Assert.Equal("Edited", elements[1].GetProperty("Origin").GetString());
        Assert.Equal(chordGroupId, elements[1].GetProperty("ChordGroupId").GetGuid());
    }

    [Fact]
    public void ToCsv_FromPianoRollNotes_ProducesExpectedHeaderAndRows()
    {
        Guid chordGroupId = Guid.NewGuid();
        var notes = new[]
        {
            new PianoRollNote(Guid.NewGuid(), 64, 1, 0.5, Velocity: 90, Origin: PianoRollNoteOrigin.SeededFromTranscript, ChordGroupId: chordGroupId),
            new PianoRollNote(Guid.NewGuid(), 60, 0, 1, Velocity: 110, Origin: PianoRollNoteOrigin.Drawn),
        };

        string csv = TranscriptExporter.ToCsv(notes, bpm: 120);
        string[] lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(Header, lines[0]);
        // Sorted by start - the solo Drawn note (beat 0) comes first; ms at 120 BPM, beats last.
        Assert.Equal("60,C4,0.0,500.0,110,Drawn,,0,1", lines[1]);
        Assert.Equal($"64,E4,500.0,250.0,90,SeededFromTranscript,{chordGroupId},1,0.5", lines[2]);
    }

    [Fact]
    public void ToCsv_FromEmptyPianoRollNotes_ProducesOnlyTheHeader()
    {
        string csv = TranscriptExporter.ToCsv(Array.Empty<PianoRollNote>(), bpm: 120);
        string[] lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Single(lines);
        Assert.Equal(Header, lines[0]);
    }

    [Fact]
    public void ToCsv_FromPianoRollNotes_MsFollowTheTempo()
    {
        var notes = new[] { new PianoRollNote(Guid.NewGuid(), 60, 2, 1) };
        string[] lines = TranscriptExporter.ToCsv(notes, bpm: 60).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("60,C4,2000.0,1000.0,100,SeededFromTranscript,,2,1", lines[1]);
    }

    private const string Header = "MidiNote,NoteName,StartTimeMs,DurationMs,Velocity,Origin,ChordGroupId,StartBeat,DurationBeats";
}
