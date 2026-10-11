using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class ChordBuilderTests
{
    [Theory]
    [InlineData(ChordQuality.Major, new[] { 60, 64, 67 })]        // C4 major: C, E, G
    [InlineData(ChordQuality.Minor, new[] { 60, 63, 67 })]        // C4 minor: C, Eb, G
    [InlineData(ChordQuality.Power, new[] { 60, 67 })]            // C4 power: C, G
    [InlineData(ChordQuality.Diminished, new[] { 60, 63, 66 })]   // C4 dim: C, Eb, Gb
    [InlineData(ChordQuality.Augmented, new[] { 60, 64, 68 })]    // C4 aug: C, E, G#
    [InlineData(ChordQuality.Sus2, new[] { 60, 62, 67 })]         // C4 sus2: C, D, G
    [InlineData(ChordQuality.Sus4, new[] { 60, 65, 67 })]         // C4 sus4: C, F, G
    [InlineData(ChordQuality.Dominant7th, new[] { 60, 64, 67, 70 })] // C, E, G, Bb
    [InlineData(ChordQuality.Major7th, new[] { 60, 64, 67, 71 })]    // C, E, G, B
    [InlineData(ChordQuality.Minor7th, new[] { 60, 63, 67, 70 })]    // C, Eb, G, Bb
    public void BuildChord_OnMiddleC_ReturnsExpectedMidiNotes(ChordQuality quality, int[] expected)
    {
        int[] actual = ChordBuilder.BuildChord(rootMidiNote: 60, quality);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildChord_NearOctaveBoundary_DoesNotWrapWithinOctave()
    {
        // B3 (59) + Augmented (0,4,8) must climb into the next octave, not wrap back down.
        int[] notes = ChordBuilder.BuildChord(rootMidiNote: 59, ChordQuality.Augmented);

        Assert.Equal(new[] { 59, 63, 67 }, notes);
        Assert.True(notes.SequenceEqual(notes.OrderBy(n => n)), "Chord tones must be monotonically increasing from the root.");
    }

    [Theory]
    [InlineData(ChordQuality.Dominant7th)]
    [InlineData(ChordQuality.Major7th)]
    [InlineData(ChordQuality.Minor7th)]
    public void BuildChord_SeventhChords_SpanMoreThanAnOctave_WithoutWrapping(ChordQuality quality)
    {
        int[] notes = ChordBuilder.BuildChord(rootMidiNote: 108, quality); // high root, offsets should just keep climbing
        Assert.All(notes, n => Assert.True(n >= 108));
    }

    [Fact]
    public void BuildChord_RootIsAlwaysFirstNote()
    {
        foreach (ChordQuality quality in Enum.GetValues<ChordQuality>())
        {
            int[] notes = ChordBuilder.BuildChord(rootMidiNote: 45, quality);
            Assert.Equal(45, notes[0]);
        }
    }
}
