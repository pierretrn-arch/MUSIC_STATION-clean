using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class RollEditingTests
{
    private static PianoRollNote N(int midi, double start, double length, Guid? chord = null) =>
        new(Guid.NewGuid(), midi, start, length, ChordGroupId: chord);

    [Fact]
    public void Split_KeepsTheLeftIdAndMakesANewRight()
    {
        PianoRollNote note = N(60, 1, 2);
        var (left, right) = RollEditing.Split(note, 2.25)!.Value;

        Assert.Equal(note.Id, left.Id);
        Assert.Equal(1.0, left.StartBeat);
        Assert.Equal(1.25, left.DurationBeats, 9);
        Assert.NotEqual(note.Id, right.Id);
        Assert.Equal(2.25, right.StartBeat);
        Assert.Equal(0.75, right.DurationBeats, 9);
        Assert.Equal(60, right.MidiNumber);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    [InlineData(0.5)]
    [InlineData(3.5)]
    public void Split_OutsideOrOnTheEdges_DoesNothing(double at) =>
        Assert.Null(RollEditing.Split(N(60, 1, 2), at));

    [Fact]
    public void PasteAt_MovesTheEarliestNoteToThePositionAndKeepsSpacing()
    {
        Guid chord = Guid.NewGuid();
        var copied = new[] { N(64, 3, 1, chord), N(60, 2, 1), N(67, 3, 1, chord) };

        IReadOnlyList<PianoRollNote> pasted = RollEditing.PasteAt(copied, 8);

        Assert.Equal(new[] { 8.0, 9.0, 9.0 }, pasted.Select(n => n.StartBeat));
        Assert.Equal(new[] { 60, 64, 67 }, pasted.Select(n => n.MidiNumber));
        Assert.All(pasted, p => Assert.DoesNotContain(copied, c => c.Id == p.Id));
        Assert.Null(pasted[0].ChordGroupId);
        Assert.NotNull(pasted[1].ChordGroupId);
        Assert.Equal(pasted[1].ChordGroupId, pasted[2].ChordGroupId);
        Assert.NotEqual(chord, pasted[1].ChordGroupId);
    }

    [Fact]
    public void PasteAt_WithNothingCopied_IsEmpty() =>
        Assert.Empty(RollEditing.PasteAt(Array.Empty<PianoRollNote>(), 4));

    [Fact]
    public void InBox_PicksNotesOverlappingInTimeAndPitch()
    {
        PianoRollNote inside = N(62, 1, 1), touchesEdge = N(64, 2.5, 2), tooHigh = N(70, 1, 1), before = N(62, 0, 1);

        IReadOnlyList<Guid> ids = RollEditing.InBox(new[] { inside, touchesEdge, tooHigh, before }, 3, 1, 65, 60);

        Assert.Equal(new[] { inside.Id, touchesEdge.Id }, ids);
    }

    [Theory]
    [InlineData(10.0, true, 4.0, 4.0)]
    [InlineData(4.0, true, 4.0, 0.0)]
    [InlineData(10.0, false, 4.0, 0.0)]
    [InlineData(2.0, true, 4.0, 4.0)]
    public void StopTarget_GoesToLoopStartThenZero(double position, bool loop, double loopStart, double expected) =>
        Assert.Equal(expected, RollEditing.StopTarget(position, loop, loopStart));

    [Fact]
    public void DefaultLoop_RunsFromBarOneToTheEndOfTheLastBarWithNotes()
    {
        Assert.Equal((0.0, 4.0), RollEditing.DefaultLoop(Array.Empty<PianoRollNote>()));
        Assert.Equal((0.0, 8.0), RollEditing.DefaultLoop(new[] { N(60, 5, 1) }));
        Assert.Equal((0.0, 8.0), RollEditing.DefaultLoop(new[] { N(60, 7, 1) }));
        Assert.Equal((0.0, 12.0), RollEditing.DefaultLoop(new[] { N(60, 7, 1.5) }));
    }
}

public class NoteDetectionDataTests
{
    [Fact]
    public void Split_DropsConfidenceAndCents()
    {
        var note = new PianoRollNote(Guid.NewGuid(), 60, 0, 2, Confidence: 0.9, CentsDeviation: -3);
        var (left, right) = RollEditing.Split(note, 1)!.Value;
        Assert.Null(left.Confidence);
        Assert.Null(right.CentsDeviation);
    }

    [Theory]
    [InlineData(60, ChordQuality.Major, "C Major")]
    [InlineData(61, ChordQuality.Minor, "C# Minor")]
    [InlineData(67, ChordQuality.Dominant7th, "G Dominant 7th")]
    [InlineData(69, ChordQuality.Sus4, "A Sus4")]
    public void ChordName_IsRootAndQuality(int root, ChordQuality quality, string expected) =>
        Assert.Equal(expected, ChordBuilder.ChordName(root, quality));
}
