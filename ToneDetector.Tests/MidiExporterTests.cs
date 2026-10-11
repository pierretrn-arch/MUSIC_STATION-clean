using System.IO;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class MidiExporterTests
{
    [Fact]
    public void BuildMidiFile_WithThreeNotes_RoundTripsWithCorrectPitchesAndDurations()
    {
        var transcript = new Transcript("test", new[]
        {
            new TranscriptEvent(TranscriptEventType.Note, 0, 500, new NoteInfo(60, 0.0, 261.63, 0.9)),
            new TranscriptEvent(TranscriptEventType.Silence, 500, 200, null),
            new TranscriptEvent(TranscriptEventType.Note, 700, 500, new NoteInfo(64, 0.0, 329.63, 0.8)),
            new TranscriptEvent(TranscriptEventType.Note, 1200, 500, new NoteInfo(67, 0.0, 392.0, 0.7)),
        });

        MidiFile midiFile = MidiExporter.BuildMidiFile(transcript, bpm: 120.0);

        using var stream = new MemoryStream();
        midiFile.Write(stream);
        stream.Position = 0;
        MidiFile reparsed = MidiFile.Read(stream);

        var notes = reparsed.GetNotes().OrderBy(n => n.Time).ToList();
        Assert.Equal(3, notes.Count);
        Assert.Equal(60, (int)notes[0].NoteNumber);
        Assert.Equal(64, (int)notes[1].NoteNumber);
        Assert.Equal(67, (int)notes[2].NoteNumber);

        // 500ms at 120 BPM, 480 ticks/quarter -> 480 ticks.
        Assert.True(Math.Abs(notes[0].Length - 480) <= 1, $"Expected ~480 ticks, got {notes[0].Length}");
    }

    [Fact]
    public void BuildMidiFile_WithEmptyTranscript_ProducesValidFileWithNoNotes()
    {
        var transcript = new Transcript("empty", Array.Empty<TranscriptEvent>());

        MidiFile midiFile = MidiExporter.BuildMidiFile(transcript);

        using var stream = new MemoryStream();
        midiFile.Write(stream); // must not throw

        stream.Position = 0;
        MidiFile reparsed = MidiFile.Read(stream);
        Assert.Empty(reparsed.GetNotes());
    }

    [Fact]
    public void BuildMidiFile_WithSimultaneousChordVoices_ProducesConcurrentNoteOns()
    {
        // PHASE2-FIXES.md Change 7: verify polyphony works BEFORE assuming a fix is needed.
        // A 3-voice chord (one root transformed via ChordBuilder) — all three voices share the
        // exact same StartTimeMs/DurationMs, exactly as ApplyChordTransform produces them.
        Guid chordGroupId = Guid.NewGuid();
        int[] chordNotes = ChordBuilder.BuildChord(60, ChordQuality.Minor); // C, Eb, G
        var transcript = new Transcript("test", chordNotes
            .Select(midi => new TranscriptEvent(TranscriptEventType.Note, 1000, 500, new NoteInfo(midi, 0.0, 0.0, 0.9, ChordGroupId: chordGroupId)))
            .ToArray());

        MidiFile midiFile = MidiExporter.BuildMidiFile(transcript, bpm: 120.0);

        using var stream = new MemoryStream();
        midiFile.Write(stream);
        stream.Position = 0;
        MidiFile reparsed = MidiFile.Read(stream);

        var notes = reparsed.GetNotes().OrderBy(n => n.NoteNumber).ToList();
        Assert.Equal(3, notes.Count);
        Assert.Equal(chordNotes.OrderBy(n => n), notes.Select(n => (int)n.NoteNumber));

        // All three must start at the identical tick (true simultaneity, not serialized
        // note-off-then-note-on) and share the same length.
        long expectedStartTicks = notes[0].Time;
        Assert.All(notes, n => Assert.Equal(expectedStartTicks, n.Time));
        Assert.All(notes, n => Assert.Equal(notes[0].Length, n.Length));

        // Re-derive raw MIDI events and confirm all three Note On events land on the same tick
        // (i.e. DryWetMidi didn't serialize any note-off before the next note-on for the group).
        var noteOnTicks = reparsed.GetTrackChunks()
            .SelectMany(chunk => chunk.Events)
            .Where(ev => ev.EventType == Melanchall.DryWetMidi.Core.MidiEventType.NoteOn)
            .Cast<Melanchall.DryWetMidi.Core.NoteOnEvent>()
            .Where(ev => ev.Velocity > 0)
            .ToList();
        Assert.Equal(3, noteOnTicks.Count);
    }

    [Fact]
    public void BuildMidiFile_WithNoBpmSpecified_DefaultsTo120()
    {
        var transcript = new Transcript("test", new[]
        {
            new TranscriptEvent(TranscriptEventType.Note, 0, 500, new NoteInfo(60, 0.0, 261.63, 0.9)),
        });

        MidiFile midiFile = MidiExporter.BuildMidiFile(transcript);

        using var stream = new MemoryStream();
        midiFile.Write(stream);
        stream.Position = 0;
        MidiFile reparsed = MidiFile.Read(stream);

        Note note = reparsed.GetNotes().Single();
        Assert.True(Math.Abs(note.Length - 480) <= 1);
    }

    // ---- PianoRollNote overload - Play/Export on both tabs use the roll's notes, which are in
    // beats, so they're written on their beats with the tab's tempo and 4/4. ----

    [Fact]
    public void BuildMidiFile_FromPianoRollNotes_RoundTripsPitchDurationAndVelocity()
    {
        var notes = new[]
        {
            new PianoRollNote(Guid.NewGuid(), 60, 0, 1, Velocity: 110),
            new PianoRollNote(Guid.NewGuid(), 64, 1.5, 1, Velocity: 90),
        };

        MidiFile reparsed = Reparse(MidiExporter.BuildMidiFile(notes, bpm: 120.0));

        var reparsedNotes = reparsed.GetNotes().OrderBy(n => n.Time).ToList();
        Assert.Equal(2, reparsedNotes.Count);
        Assert.Equal(60, (int)reparsedNotes[0].NoteNumber);
        Assert.Equal(110, (int)reparsedNotes[0].Velocity);
        Assert.Equal(64, (int)reparsedNotes[1].NoteNumber);
        Assert.Equal(90, (int)reparsedNotes[1].Velocity);

        // One beat = 480 ticks/quarter; 1.5 beats = 720 ticks.
        Assert.Equal(480, reparsedNotes[0].Length);
        Assert.Equal(720, reparsedNotes[1].Time);
    }

    [Fact]
    public void BuildMidiFile_FromPianoRollNotes_WithOverlappingStartTimes_ProducesConcurrentNoteOns()
    {
        // Unlike a transcript chord (voices tied to the exact same start), Piano Roll notes can
        // overlap at genuinely different start times - DryWetMidi must still interleave the
        // on/off events correctly regardless.
        var notes = new[]
        {
            new PianoRollNote(Guid.NewGuid(), 60, 0, 2, Velocity: 100),
            new PianoRollNote(Guid.NewGuid(), 64, 0.4, 1.2, Velocity: 100),
        };

        MidiFile reparsed = Reparse(MidiExporter.BuildMidiFile(notes, bpm: 120.0));

        var reparsedNotes = reparsed.GetNotes().OrderBy(n => n.NoteNumber).ToList();
        Assert.Equal(2, reparsedNotes.Count);
        Assert.True(reparsedNotes[1].Time > reparsedNotes[0].Time); // starts later
        Assert.True(reparsedNotes[1].Time < reparsedNotes[0].Time + reparsedNotes[0].Length); // while the first is still sounding
    }

    [Fact]
    public void BuildMidiFile_FromPianoRollNotes_WritesTempoAndFourFour()
    {
        var notes = new[] { new PianoRollNote(Guid.NewGuid(), 60, 4, 1) };

        MidiFile reparsed = Reparse(MidiExporter.BuildMidiFile(notes, bpm: 90.0));

        Assert.Equal(480, ((TicksPerQuarterNoteTimeDivision)reparsed.TimeDivision).TicksPerQuarterNote);
        TempoMap tempoMap = reparsed.GetTempoMap();
        Assert.Equal(90.0, tempoMap.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute, 3);
        TimeSignature signature = tempoMap.GetTimeSignatureAtTime(new MidiTimeSpan(0));
        Assert.Equal(4, signature.Numerator);
        Assert.Equal(4, signature.Denominator);

        // Beat 4 = bar 2, which at 90 BPM is 2666.7 ms in.
        Note note = reparsed.GetNotes().Single();
        Assert.Equal(1920, note.Time);
        Assert.InRange(note.TimeAs<MetricTimeSpan>(tempoMap).TotalMilliseconds, 2665, 2668);
    }

    private static MidiFile Reparse(MidiFile file)
    {
        using var stream = new MemoryStream();
        file.Write(stream);
        stream.Position = 0;
        return MidiFile.Read(stream);
    }
}
