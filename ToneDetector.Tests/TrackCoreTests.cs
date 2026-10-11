using System.IO;
using System.Text.Json;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class MixerMathTests
{
    [Theory]
    [InlineData(0.8, 0.0)]
    [InlineData(1.0, 6.0)]
    [InlineData(0.9, 3.0)]
    [InlineData(0.4, -12.041)]
    public void PositionToDb_HasUnityAt80Percent(double position, double db) =>
        Assert.Equal(db, MixerMath.PositionToDb(position), 2);

    [Fact]
    public void ZeroIsSilent()
    {
        Assert.True(double.IsNegativeInfinity(MixerMath.PositionToDb(0)));
        Assert.Equal(0.0, MixerMath.PositionToGain(0));
        Assert.Equal("−∞", MixerMath.DbText(0));
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    [InlineData(0.95)]
    public void DbAndPosition_RoundTrip(double position) =>
        Assert.Equal(position, MixerMath.DbToPosition(MixerMath.PositionToDb(position)), 9);

    [Theory]
    [InlineData(0.8, "0.0")]
    [InlineData(1.0, "+6.0")]
    [InlineData(0.4, "−12.0")]
    public void DbText_UsesTheRealMinusSign(double position, string expected) =>
        Assert.Equal(expected, MixerMath.DbText(position));

    [Fact]
    public void Gain_IsOneAtUnity() => Assert.Equal(1.0, MixerMath.PositionToGain(0.8), 9);

    [Theory]
    [InlineData(0, "C")]
    [InlineData(-20, "L20")]
    [InlineData(15, "R15")]
    [InlineData(250, "R100")]
    public void PanText_IsCOrLOrR(int pan, string expected) => Assert.Equal(expected, MixerMath.PanText(pan));

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, true, true, false)]
    public void IsAudible_FollowsMuteAndSolo(bool muted, bool solo, bool anySolo, bool expected) =>
        Assert.Equal(expected, MixerMath.IsAudible(muted, solo, anySolo));

    [Fact]
    public void MidiControllers_ScaleVolumeAndPan()
    {
        Assert.Equal(127, MixerMath.MidiVolume(0.8));
        Assert.Equal(127, MixerMath.MidiVolume(1.0));
        Assert.Equal(64, MixerMath.MidiVolume(0.4031));
        Assert.Equal((0, 64, 127), (MixerMath.MidiPan(-100), MixerMath.MidiPan(0), MixerMath.MidiPan(100)));
    }

    [Fact]
    public void Palette_HasEightColoursAndRepeats()
    {
        Assert.Equal(8, TrackPalette.Colours.Count);
        Assert.Equal("#F2A541", TrackPalette.Colours[0]);
        Assert.Equal((0, 7, 0, 1), (TrackPalette.IndexFor(0), TrackPalette.IndexFor(7), TrackPalette.IndexFor(8), TrackPalette.IndexFor(9)));
    }
}

public class MultiTrackExportTests
{
    private static TrackExport Track(string name, bool drums = false, int pan = 0, double volume = 0.8, params (int Midi, double Start, double Length)[] notes) =>
        new(name, "#F2A541", "Built-in synth", drums, false, false, volume, pan,
            notes.Select(n => new PianoRollNote(Guid.NewGuid(), n.Midi, n.Start, n.Length)).ToList());

    private static MidiFile Reparse(MidiFile file)
    {
        using var stream = new MemoryStream();
        file.Write(stream, MidiFileFormat.MultiTrack);
        stream.Position = 0;
        return MidiFile.Read(stream);
    }

    [Fact]
    public void Channels_GoInOrder_SkippingTen_WithDrumsOnTen()
    {
        var tracks = Enumerable.Range(0, 11).Select(i => Track($"T{i}")).Append(Track("Kit", drums: true)).ToList();

        int[] channels = MidiExporter.AssignChannels(tracks);

        Assert.Equal(new[] { 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 11, 9 }, channels);
    }

    [Fact]
    public void Midi_WritesOneNamedTrackPerTrack_OnItsOwnChannel()
    {
        var tracks = new[]
        {
            Track("Piano", pan: -50, notes: new[] { (60, 0.0, 1.0), (64, 1.0, 1.0) }),
            Track("Drums", drums: true, notes: new[] { (36, 0.0, 0.25) }),
        };

        MidiFile file = Reparse(MidiExporter.BuildMidiFile(tracks, 100));
        var chunks = file.GetTrackChunks().ToList();

        Assert.Equal(3, chunks.Count); // tempo track + two tracks
        Assert.Equal(100.0, file.GetTempoMap().GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute, 3);
        Assert.Equal("Piano", chunks[1].Events.OfType<SequenceTrackNameEvent>().Single().Text);
        Assert.All(chunks[1].GetNotes(), n => Assert.Equal(0, (int)n.Channel));
        Assert.Equal(9, (int)chunks[2].GetNotes().Single().Channel);
        ControlChangeEvent pan = chunks[1].Events.OfType<ControlChangeEvent>().Single(c => c.ControlNumber == 10);
        Assert.Equal(MixerMath.MidiPan(-50), (int)pan.ControlValue);
        Assert.Equal(480, chunks[1].GetNotes().ElementAt(1).Time);
    }

    [Fact]
    public void Midi_ReadsBackAsParts()
    {
        var tracks = new[] { Track("Lead", notes: new[] { (72, 2.0, 0.5) }), Track("Bass", notes: new[] { (36, 0.0, 4.0) }) };

        MidiFileContents contents = MidiImporter.ReadParts(Reparse(MidiExporter.BuildMidiFile(tracks, 120)));

        Assert.Equal(new[] { "Lead", "Bass" }, contents.Parts.Select(p => p.Name));
        Assert.Equal(2.0, contents.Parts[0].Notes.Single().StartBeat, 6);
    }

    [Fact]
    public void Json_ListsTracksWithMixerAndNotes()
    {
        var tracks = new[] { Track("Piano", pan: 20, volume: 0, notes: new[] { (60, 0.0, 1.0) }), Track("Empty") };

        using JsonDocument doc = JsonDocument.Parse(TranscriptExporter.ToJson(tracks, 120));
        JsonElement[] json = doc.RootElement.GetProperty("Tracks").EnumerateArray().ToArray();

        Assert.Equal(2, json.Length);
        Assert.Equal("Piano", json[0].GetProperty("Name").GetString());
        Assert.Equal(20, json[0].GetProperty("Pan").GetInt32());
        Assert.Equal(JsonValueKind.Null, json[0].GetProperty("VolumeDb").ValueKind); // silent
        Assert.Equal(500.0, json[0].GetProperty("Notes")[0].GetProperty("DurationMs").GetDouble());
        Assert.Equal(0.0, json[1].GetProperty("VolumeDb").GetDouble());
        Assert.Empty(json[1].GetProperty("Notes").EnumerateArray());
    }

    [Fact]
    public void Csv_HasATrackColumn_TrackByTrack()
    {
        var tracks = new[] { Track("Lead, main", notes: new[] { (72, 1.0, 1.0), (71, 0.0, 1.0) }), Track("Bass", notes: new[] { (36, 0.0, 1.0) }) };

        string[] lines = TranscriptExporter.ToCsv(tracks, 120).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.StartsWith("Track,MidiNote,", lines[0]);
        Assert.StartsWith("\"Lead, main\",71,", lines[1]);
        Assert.StartsWith("\"Lead, main\",72,", lines[2]);
        Assert.StartsWith("Bass,36,", lines[3]);
    }
}
