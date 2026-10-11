using ToneDetector.App.ViewModels;
using ToneDetector.App.Views;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>File Analysis results panel rows, session-bar summary and piano-roll notes.</summary>
public class FileAnalysisResultsTests
{
    private static TranscriptEvent Note(int midi, double startMs, double durationMs, double cents = 2.4, double f0 = 440.0, double confidence = 0.97) =>
        new(TranscriptEventType.Note, startMs, durationMs, new NoteInfo(midi, cents, f0, confidence));

    private static TranscriptEvent Silence(double startMs, double durationMs) =>
        new(TranscriptEventType.Silence, startMs, durationMs, null);

    [Fact]
    public void NoteRow_FormatsEveryColumnAndTile()
    {
        AnalysisResultRow row = AnalysisResultRow.FromEvent(Note(61, 300.4, 499.6, cents: -6.2, f0: 277.18, confidence: 0.914));

        Assert.Equal("Note", row.TypeText);
        Assert.Equal("C#4", row.NoteText);
        Assert.Equal("300", row.StartText);
        Assert.Equal("500", row.DurationText);
        Assert.Equal("0.91", row.ConfidenceText);
        Assert.Equal("−6", row.CentsText);
        Assert.Equal("277.18", row.MeanF0Text);
        Assert.Equal(0.914 * 48, row.ConfidenceBarWidth, precision: 6);
    }

    [Fact]
    public void SilenceRow_HasDashesAndNoBar()
    {
        AnalysisResultRow row = AnalysisResultRow.FromEvent(Silence(800, 120));

        Assert.Equal("Silence", row.TypeText);
        Assert.Equal("—", row.NoteText);
        Assert.Equal("—", row.ConfidenceText);
        Assert.Equal("—", row.CentsText);
        Assert.Null(row.Confidence);
        Assert.Equal(0.0, row.ConfidenceBarWidth);
    }

    [Fact]
    public void Summary_IsTheEndTimeAndNoteCount()
    {
        var events = new[] { Note(64, 300, 500), Silence(800, 200), Note(67, 1000, 10840) };

        Assert.Equal("00:11.840 · 2 notes", FileAnalysisViewModel.Summarize(events));
        Assert.Equal("00:00.800 · 1 note", FileAnalysisViewModel.Summarize(new[] { Note(64, 300, 500) }));
        Assert.Equal("00:00.000 · 0 notes", FileAnalysisViewModel.Summarize(Array.Empty<TranscriptEvent>()));
    }

    [Fact]
    public void RollNotes_AreTheNoteEventsOnly_InBeatsAtTheTempo()
    {
        var notes = FileAnalysisViewModel.ToRollNotes(new[] { Note(64, 300, 500), Silence(800, 200), Note(67, 1000, 400) }, bpm: 120).ToList();

        // 120 BPM: 500 ms a beat.
        Assert.Equal(2, notes.Count);
        Assert.Equal((64, 0.6, 1.0), (notes[0].MidiNumber, notes[0].StartBeat, notes[0].DurationBeats));
        Assert.Equal((67, 2.0, 0.8), (notes[1].MidiNumber, notes[1].StartBeat, notes[1].DurationBeats));
        Assert.NotEqual(notes[0].Id, notes[1].Id);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "1 note selected")]
    [InlineData(3, "3 notes selected")]
    public void SelectionSummary_CountsNotes(int count, string expected)
    {
        Assert.Equal(expected, EditorToolbar.SelectionSummary(count));
    }

    [Fact]
    public void ChordQualities_FollowTheEnumOrder()
    {
        Assert.Equal(Enum.GetNames<ChordQuality>(), EditorOptions.ChordQualities);
        Assert.Equal("Major", EditorOptions.ChordQualities[0]);
    }

    [Fact]
    public void OpenFileFilter_ListsAudioAndMidiFormats()
    {
        string filter = FileAnalysisViewModel.OpenFileFilter;
        string[] parts = filter.Split('|');

        Assert.Equal(8, parts.Length); // four label|pattern pairs
        Assert.Equal("Audio and MIDI files", parts[0]);
        foreach (string ext in new[] { "*.m4a", "*.mid", "*.midi", "*.aiff", "*.wav", "*.mp3", "*.flac" })
            Assert.Contains(ext, parts[1].Split(';'));
        Assert.Equal("MIDI files (*.mid;*.midi)", parts[4]);
        Assert.Equal("*.*", parts[7]);
    }

    [Theory]
    [InlineData(@"C:\music\song.mid", true)]
    [InlineData(@"C:\music\song.MIDI", true)]
    [InlineData(@"C:\music\song.m4a", false)]
    [InlineData(@"C:\music\song.mid.wav", false)]
    public void IsMidiFile_GoesByExtension(string path, bool expected) =>
        Assert.Equal(expected, FileAnalysisViewModel.IsMidiFile(path));

    [Fact]
    public void RollNotes_VelocityFollowsConfidence()
    {
        PianoRollNote note = FileAnalysisViewModel.ToRollNotes(new[] { Note(60, 0, 500, confidence: 1.0), Note(62, 500, 500, confidence: 0.0) }, 120).First();
        Assert.Equal(127, note.Velocity);
        Assert.Equal(32, FileAnalysisViewModel.ToRollNotes(new[] { Note(62, 500, 500, confidence: 0.0) }, 120).Single().Velocity);
    }

    [Fact]
    public void DetectTempo_UsesTheNoteOnsets_OrFallsBackTo120()
    {
        // Onsets every 500 ms = 120 BPM; every 750 ms = 80 BPM.
        var steady = Enumerable.Range(0, 10).Select(i => Note(60, i * 750, 700)).ToArray();
        Assert.Equal(80.0, FileAnalysisViewModel.DetectTempo(steady));
        Assert.Equal(120.0, FileAnalysisViewModel.DetectTempo(new[] { Note(60, 0, 500), Note(62, 900, 500) }));
    }

    [Fact]
    public void MidiNotesAsTranscript_AreExactAndTimedAtTheFileTempo()
    {
        var notes = new[] { new PianoRollNote(Guid.NewGuid(), 69, 2, 1) };

        TranscriptEvent evt = FileAnalysisViewModel.ToTranscript(notes, 60, "a.mid").Events.Single();

        Assert.Equal((2000.0, 1000.0), (evt.StartTimeMs, evt.DurationMs));
        Assert.Equal(440.0, evt.Note!.Value.MeanF0Hz, 6);
        Assert.Equal(1.0, evt.Note.Value.ConfidenceMean);
    }
}
