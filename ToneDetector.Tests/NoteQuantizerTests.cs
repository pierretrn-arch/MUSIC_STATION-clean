using ToneDetector.Core.Pitch;
using Xunit;

namespace ToneDetector.Tests;

public class NoteQuantizerTests
{
    [Fact]
    public void FrequencyToAbsoluteCents_AtConcertPitch_MatchesMidi69Times100()
    {
        double cents = NoteQuantizer.FrequencyToAbsoluteCents(440.0);
        Assert.Equal(6900.0, cents, precision: 6);
    }

    [Theory]
    [InlineData(440.0, 69, 0.0)]
    [InlineData(261.6256, 60, 0.0)] // C4 (middle C)
    public void QuantizeToNearestNote_OnExactFrequency_HasNearZeroCentsDeviation(
        double frequencyHz, int expectedMidiNote, double expectedCents)
    {
        double absoluteCents = NoteQuantizer.FrequencyToAbsoluteCents(frequencyHz);
        (int midiNote, double centsDeviation) = NoteQuantizer.QuantizeToNearestNote(absoluteCents);

        Assert.Equal(expectedMidiNote, midiNote);
        Assert.True(Math.Abs(centsDeviation - expectedCents) < 0.5);
    }

    [Fact]
    public void QuantizeToNearestNote_SlightlySharp_ReportsPositiveCentsDeviation()
    {
        double slightlySharp = 440.0 * Math.Pow(2.0, 10.0 / 1200.0); // +10 cents sharp of A4
        double absoluteCents = NoteQuantizer.FrequencyToAbsoluteCents(slightlySharp);
        (int midiNote, double centsDeviation) = NoteQuantizer.QuantizeToNearestNote(absoluteCents);

        Assert.Equal(69, midiNote);
        Assert.True(Math.Abs(centsDeviation - 10.0) < 0.1);
    }

    [Theory]
    [InlineData(69, "A4")]
    [InlineData(60, "C4")]
    [InlineData(0, "C-1")]
    public void MidiNoteName_ReturnsStandardName(int midiNote, string expectedName)
    {
        Assert.Equal(expectedName, NoteQuantizer.MidiNoteName(midiNote));
    }

    [Fact]
    public void AbsoluteCentsToFrequency_RoundTripsWithFrequencyToAbsoluteCents()
    {
        double original = 220.0;
        double cents = NoteQuantizer.FrequencyToAbsoluteCents(original);
        double roundTripped = NoteQuantizer.AbsoluteCentsToFrequency(cents);

        Assert.True(Math.Abs(roundTripped - original) < 1e-6);
    }

    [Theory]
    [InlineData("C", 0)]
    [InlineData("c", 0)]
    [InlineData("C#", 1)]
    [InlineData("Cs", 1)]
    [InlineData("Db", 1)]
    [InlineData("B", 11)]
    [InlineData("Cb", 11)] // wraps below C down to B
    [InlineData("B#", 0)]  // wraps above B up to C
    public void TryParseNoteName_OnValidInput_ReturnsExpectedPitchClass(string text, int expectedPitchClass)
    {
        bool ok = NoteQuantizer.TryParseNoteName(text, out int pitchClass);

        Assert.True(ok);
        Assert.Equal(expectedPitchClass, pitchClass);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("H")]
    [InlineData("C##x")]
    [InlineData(null)]
    public void TryParseNoteName_OnInvalidInput_ReturnsFalseWithoutThrowing(string? text)
    {
        bool ok = NoteQuantizer.TryParseNoteName(text, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryComputeMidiNumber_CombinesNoteAndOctave()
    {
        bool ok = NoteQuantizer.TryComputeMidiNumber("C", 4, out int midiNumber);

        Assert.True(ok);
        Assert.Equal(60, midiNumber); // middle C
    }

    [Fact]
    public void TryComputeMidiNumber_OnInvalidNoteName_ReturnsFalse()
    {
        bool ok = NoteQuantizer.TryComputeMidiNumber("H", 4, out _);

        Assert.False(ok);
    }
}
