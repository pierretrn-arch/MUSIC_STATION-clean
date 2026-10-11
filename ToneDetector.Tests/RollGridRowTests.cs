using ToneDetector.App.ViewModels;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Live Tuner side panel Grid rows, and the detected note the piano roll highlights.</summary>
public class RollGridRowTests
{
    private static PianoRollNote Note(int midi, double startBeat, double beats, double? confidence = null, double? cents = null, string? chord = null) =>
        new(Guid.NewGuid(), midi, startBeat, beats, Confidence: confidence, CentsDeviation: cents, ChordName: chord);

    [Fact]
    public void NoteRows_AreInTimeOrder_InMsAtTheTempo()
    {
        var late = Note(67, 3.4, 1);
        var first = Note(64, 0, 1.2);
        var chordLow = Note(60, 3, 1.2);
        var chordHigh = Note(64, 3, 1.2);

        var rows = RollGridRow.Build(new[] { late, chordHigh, first, chordLow }, bpm: 120);

        // 120 BPM: 500 ms a beat.
        Assert.Equal(new[] { first.Id, chordLow.Id, chordHigh.Id, late.Id }, rows.Select(r => r.NoteId));
        Assert.Equal(new[] { "E4", "C4", "E4", "G4" }, rows.Select(r => r.NoteText));
        Assert.Equal(new[] { "0", "1500", "1500", "1700" }, rows.Select(r => r.StartText));
        Assert.Equal("600", rows[0].DurationText);
    }

    [Fact]
    public void Tempo_ChangesTheMs()
    {
        var rows = RollGridRow.Build(new[] { Note(60, 2, 1) }, bpm: 60);
        Assert.Equal(("2000", "1000"), (rows[0].StartText, rows[0].DurationText));
    }

    [Fact]
    public void SilenceRows_FillTheGaps_UnlessHidden()
    {
        var notes = new[] { Note(60, 1, 1), Note(62, 2, 1), Note(64, 4, 1) }; // gaps: 0-1 and 3-4 beats

        var shown = RollGridRow.Build(notes, 120, hideSilence: false);
        Assert.Equal(new[] { "Silence", "Note", "Note", "Silence", "Note" }, shown.Select(r => r.TypeText));
        RollGridRow gap = shown[3];
        Assert.True(gap.IsSilence);
        Assert.Equal(Guid.Empty, gap.NoteId);
        Assert.Equal(("1500", "500", "—"), (gap.StartText, gap.DurationText, gap.NoteText));

        Assert.Equal(3, RollGridRow.Build(notes, 120).Count); // hidden by default
    }

    [Fact]
    public void OverlappingNotes_LeaveNoSilence_AndGapIsZero()
    {
        var rows = RollGridRow.Build(new[] { Note(60, 0, 4), Note(64, 1, 1), Note(67, 4, 1) }, 120, hideSilence: false);

        Assert.DoesNotContain(rows, r => r.IsSilence);
        Assert.Equal(new[] { "—", "0", "0" }, rows.Select(r => r.GapText));
    }

    [Fact]
    public void GapSincePrev_IsTheSilenceBeforeTheNote()
    {
        var rows = RollGridRow.Build(new[] { Note(60, 0, 1), Note(62, 1.5, 1) }, 120);
        Assert.Equal(new[] { "—", "250" }, rows.Select(r => r.GapText));
    }

    [Fact]
    public void DetectedNotes_ShowConfidenceCentsAndChord_OthersShowDashes()
    {
        var rows = RollGridRow.Build(new[] { Note(60, 0, 1, confidence: 0.914, cents: -6.2, chord: "C Major"), Note(62, 1, 1) }, 120);

        Assert.Equal(("0.91", "−6", "C Major"), (rows[0].ConfidenceText, rows[0].CentsText, rows[0].ChordText));
        Assert.Equal(0.914 * 44, rows[0].ConfidenceBarWidth, 6);
        Assert.Equal(("—", "—", "—"), (rows[1].ConfidenceText, rows[1].CentsText, rows[1].ChordText));
        Assert.Null(rows[1].Confidence);
        Assert.Equal(0.0, rows[1].ConfidenceBarWidth);
    }

    [Fact]
    public void Sharps_UseHash()
    {
        Assert.Equal("C#4", RollGridRow.Build(new[] { Note(61, 0, 1) }, bpm: 120)[0].NoteText);
    }

    [Fact]
    public void NoNotes_NoRows()
    {
        Assert.Empty(RollGridRow.Build(Array.Empty<PianoRollNote>(), bpm: 120, hideSilence: false));
    }

    [Theory]
    [InlineData(1, "1 note")]
    [InlineData(12, "12 notes")]
    [InlineData(0, "0 notes")]
    public void CountText_Pluralises(int count, string expected)
    {
        Assert.Equal(expected, RollGridRow.CountText(count));
    }

    [Fact]
    public void DetectedMidiNote_FollowsTheCurrentPitch()
    {
        var viewModel = new LiveTunerViewModel(
            new PipelineSettingsViewModel(),
            new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()),
            () => new FakeAudioCapture(),
            new FakeAudioDeviceManager());
        viewModel.IsPipelineRunning = true;

        Assert.Null(viewModel.DetectedMidiNote);
        viewModel.CurrentPitch = new PitchDisplay("E", 4);
        Assert.Equal(64, viewModel.DetectedMidiNote);
        viewModel.IsPipelineRunning = false;
        Assert.Null(viewModel.DetectedMidiNote);
    }
}
