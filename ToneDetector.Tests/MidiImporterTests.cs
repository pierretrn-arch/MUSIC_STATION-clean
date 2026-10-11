using System.IO;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class MidiImporterTests
{
    private static MidiFile Reparse(MidiFile file)
    {
        using var stream = new MemoryStream();
        file.Write(stream);
        stream.Position = 0;
        return MidiFile.Read(stream);
    }

    private static TrackChunk Track(string? name, params Note[] notes)
    {
        TrackChunk chunk = notes.ToTrackChunk();
        if (name is not null)
            chunk.Events.Insert(0, new SequenceTrackNameEvent(name));
        return chunk;
    }

    private static Note Note(int midi, long time, long length, int channel = 0, int velocity = 100) =>
        new((SevenBitNumber)midi, length, time) { Channel = (FourBitNumber)channel, Velocity = (SevenBitNumber)velocity };

    private static MidiFile File(double bpm, params TrackChunk[] tracks)
    {
        var file = new MidiFile(tracks) { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        file.ReplaceTempoMap(TempoMap.Create(new TicksPerQuarterNoteTimeDivision(480), Tempo.FromBeatsPerMinute(bpm)));
        return Reparse(file);
    }

    [Fact]
    public void ReadParts_GivesEachTrackItsOwnPart_InBeats()
    {
        MidiFile file = File(96,
            Track("Piano", Note(60, 0, 480), Note(64, 960, 240)),
            Track("Bass", Note(36, 1920, 1920)));

        MidiFileContents contents = MidiImporter.ReadParts(file);

        Assert.Equal(96.0, contents.Bpm, 3);
        Assert.Equal(new[] { "Piano", "Bass" }, contents.Parts.Select(p => p.Name));
        PianoRollNote[] piano = contents.Parts[0].Notes.ToArray();
        Assert.Equal(new[] { 0.0, 2.0 }, piano.Select(n => n.StartBeat));
        Assert.Equal(new[] { 1.0, 0.5 }, piano.Select(n => n.DurationBeats));
        Assert.Equal(4.0, contents.Parts[1].Notes.Single().StartBeat);
    }

    [Fact]
    public void ReadParts_SplitsChannelsAndMarksDrums()
    {
        MidiFile file = File(120, Track(null, Note(60, 0, 480, channel: 0), Note(36, 0, 120, channel: 9), Note(67, 0, 480, channel: 1)));

        MidiFileContents contents = MidiImporter.ReadParts(file);

        // One part per channel, however the file groups them into tracks.
        MidiPart[] parts = contents.Parts.OrderBy(p => p.ChannelNumber).ToArray();
        Assert.Equal(new[] { 1, 2, 10 }, parts.Select(p => p.ChannelNumber));
        Assert.Equal(new[] { false, false, true }, parts.Select(p => p.IsDrums));
        Assert.Equal("Drums (channel 10)", parts[2].Name);
        Assert.Equal(new[] { 60, 67, 36 }, parts.Select(p => p.Notes.Single().MidiNumber));
    }

    [Theory]
    [InlineData("Piano", 0, 0, false, 1, "Piano")]
    [InlineData("Piano", 0, 1, false, 2, "Piano · channel 2")]
    [InlineData(null, 2, 0, false, 1, "Track 3")]
    [InlineData(null, 0, 9, true, 1, "Drums (channel 10)")]
    [InlineData("Kit", 0, 9, true, 1, "Kit · drums (channel 10)")]
    public void PartName_DescribesTrackAndChannel(string? track, int index, int channel, bool drums, int channels, string expected) =>
        Assert.Equal(expected, MidiImporter.PartName(track, index, channel, drums, channels));

    [Fact]
    public void ReadParts_KeepsVelocityAndChords()
    {
        MidiFile file = File(120, Track("Keys", Note(67, 0, 480, velocity: 40), Note(60, 0, 480, velocity: 90), Note(64, 0, 480)));

        PianoRollNote[] notes = MidiImporter.ReadParts(file).Parts.Single().Notes.ToArray();

        Assert.Equal(new[] { 60, 64, 67 }, notes.Select(n => n.MidiNumber));
        Assert.Equal(new[] { 90, 100, 40 }, notes.Select(n => n.Velocity));
    }

    [Fact]
    public void ReadParts_ClampsTheTempo() =>
        Assert.Equal(240.0, MidiImporter.ReadParts(File(300, Track("x", Note(60, 0, 480)))).Bpm);

    [Fact]
    public void ReadParts_RoundTripsTheRollExport()
    {
        var notes = new[] { new PianoRollNote(Guid.NewGuid(), 60, 1.5, 0.25), new PianoRollNote(Guid.NewGuid(), 72, 6, 2) };

        MidiFileContents contents = MidiImporter.ReadParts(Reparse(MidiExporter.BuildMidiFile(notes, 100)));

        Assert.Equal(100.0, contents.Bpm, 3);
        MidiPart part = contents.Parts.Single();
        Assert.Equal(new[] { 1.5, 6.0 }, part.Notes.Select(n => n.StartBeat));
        Assert.Equal(new[] { 0.25, 2.0 }, part.Notes.Select(n => n.DurationBeats));
    }

    [Fact]
    public void ReadParts_WithNoNotes_HasNoParts() =>
        Assert.Empty(MidiImporter.ReadParts(Reparse(new MidiFile(new TrackChunk()))).Parts);
}
