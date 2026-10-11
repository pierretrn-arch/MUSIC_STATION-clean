using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace ToneDetector.Core.Transcript;

/// <summary>
/// Converts a <see cref="Transcript"/> into a single-track Type 0 MIDI file. Silence events
/// consume time but emit no notes. Tempo defaults to 120 BPM when the caller doesn't have a
/// detected tempo to pass in. Chord voices (notes sharing a <c>ChordGroupId</c>) need no special
/// handling here: they're just multiple <see cref="Note"/>s with identical start ticks, and
/// standard MIDI already supports polyphony natively at the same tick — DryWetMidi's
/// <c>ToTrackChunk()</c> interleaves each note's on/off events by absolute time regardless of
/// overlap, so simultaneous notes "just work" (verified by
/// <c>MidiExporterTests.BuildMidiFile_WithSimultaneousChordVoices_ProducesConcurrentNoteOns</c>).
/// <c>ChordGroupId</c> itself has no MIDI wire-format representation (it's an editor/DAW-side
/// grouping concept, not something standard MIDI files encode) and is intentionally not written.
/// </summary>
public static class MidiExporter
{
    private const int TicksPerQuarterNote = 480;
    private const double DefaultBpm = 120.0;

    /// <summary>The tempo map must carry the file's own time division: ReplaceTempoMap also
    /// overwrites <see cref="MidiFile.TimeDivision"/>, and TempoMap.Create without one defaults to
    /// 96 ticks per quarter note, which made every exported file play 5x too slow.</summary>
    private static TempoMap CreateTempoMap(double bpm) =>
        TempoMap.Create(new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote), Tempo.FromBeatsPerMinute(bpm));

    public static MidiFile BuildMidiFile(Transcript transcript, double? bpm = null)
    {
        double effectiveBpm = bpm is > 0 ? bpm.Value : DefaultBpm;
        double ticksPerMs = TicksPerQuarterNote * effectiveBpm / 60000.0;

        var notes = new List<Note>();
        foreach (TranscriptEvent evt in transcript.Events)
        {
            if (evt.Type != TranscriptEventType.Note || evt.Note is not { } noteInfo)
                continue;

            long startTicks = (long)Math.Round(evt.StartTimeMs * ticksPerMs);
            long lengthTicks = Math.Max(1, (long)Math.Round(evt.DurationMs * ticksPerMs));
            var noteNumber = (SevenBitNumber)Math.Clamp(noteInfo.MidiNumber, 0, 127);
            var velocity = (SevenBitNumber)Math.Clamp((int)(32 + 95 * noteInfo.ConfidenceMean), 32, 127);

            notes.Add(new Note(noteNumber, lengthTicks, startTicks)
            {
                Velocity = velocity,
                OffVelocity = velocity,
            });
        }

        TrackChunk trackChunk = notes.ToTrackChunk();
        var midiFile = new MidiFile(trackChunk)
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
        };
        midiFile.ReplaceTempoMap(CreateTempoMap(effectiveBpm));

        return midiFile;
    }

    public static void Export(Transcript transcript, string filePath, double? bpm = null)
    {
        MidiFile midiFile = BuildMidiFile(transcript, bpm);
        midiFile.Write(filePath, overwriteFile: true, MidiFileFormat.SingleTrack);
    }

    /// <summary>Piano Roll variant of <see cref="BuildMidiFile(Transcript, double?)"/>. Roll notes
    /// are already in beats, so they're written on their beats (ticks = beats x 480) with the tab's
    /// tempo and a 4/4 time signature, and a DAW lines them up with its own bars. Velocity comes
    /// straight from <see cref="PianoRollNote.Velocity"/>. Notes may freely overlap here, which
    /// DryWetMidi already handles correctly - see this class's own doc comment.</summary>
    public static MidiFile BuildMidiFile(IReadOnlyList<PianoRollNote> notes, double bpm)
    {
        double effectiveBpm = bpm > 0 ? bpm : DefaultBpm;

        var midiNotes = new List<Note>();
        foreach (PianoRollNote note in notes)
        {
            long startTicks = (long)Math.Round(note.StartBeat * TicksPerQuarterNote);
            long lengthTicks = Math.Max(1, (long)Math.Round(note.DurationBeats * TicksPerQuarterNote));
            var noteNumber = (SevenBitNumber)Math.Clamp(note.MidiNumber, 0, 127);
            var velocity = (SevenBitNumber)Math.Clamp(note.Velocity, 0, 127);

            midiNotes.Add(new Note(noteNumber, lengthTicks, startTicks)
            {
                Velocity = velocity,
                OffVelocity = velocity,
            });
        }

        TrackChunk trackChunk = midiNotes.ToTrackChunk();
        var midiFile = new MidiFile(trackChunk)
        {
            TimeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
        };
        midiFile.ReplaceTempoMap(TempoMap.Create(
            new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
            Tempo.FromBeatsPerMinute(effectiveBpm),
            new TimeSignature(MusicalTime.BeatsPerBar, 4)));

        return midiFile;
    }

    public static void Export(IReadOnlyList<PianoRollNote> notes, string filePath, double bpm)
    {
        MidiFile midiFile = BuildMidiFile(notes, bpm);
        midiFile.Write(filePath, overwriteFile: true, MidiFileFormat.SingleTrack);
    }

    /// <summary>Several tracks as a multi-track (Type 1) file: a tempo track (tempo, 4/4), then
    /// one track per track with its name, its own channel (drum tracks on channel 10, the others
    /// 1-16 in order, skipping 10), its volume (CC 7) and pan (CC 10), and its notes on their beats.
    /// Every track is written whatever its mute/solo state (those are for listening).</summary>
    public static MidiFile BuildMidiFile(IReadOnlyList<TrackExport> tracks, double bpm)
    {
        double effectiveBpm = bpm > 0 ? bpm : DefaultBpm;
        var timeDivision = new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote);
        int[] channels = AssignChannels(tracks);

        var chunks = new List<TrackChunk> { new() };
        for (int t = 0; t < tracks.Count; t++)
        {
            TrackExport track = tracks[t];
            var channel = (FourBitNumber)channels[t];
            var notes = track.Notes.Select(n => new Note(
                    (SevenBitNumber)Math.Clamp(n.MidiNumber, 0, 127),
                    Math.Max(1, (long)Math.Round(n.DurationBeats * TicksPerQuarterNote)),
                    (long)Math.Round(n.StartBeat * TicksPerQuarterNote))
            {
                Channel = channel,
                Velocity = (SevenBitNumber)Math.Clamp(n.Velocity, 0, 127),
                OffVelocity = (SevenBitNumber)Math.Clamp(n.Velocity, 0, 127),
            });

            TrackChunk chunk = notes.ToTrackChunk();
            chunk.Events.Insert(0, new SequenceTrackNameEvent(track.Name));
            chunk.Events.Insert(1, new ControlChangeEvent((SevenBitNumber)7, (SevenBitNumber)MixerMath.MidiVolume(track.VolumePosition)) { Channel = channel });
            chunk.Events.Insert(2, new ControlChangeEvent((SevenBitNumber)10, (SevenBitNumber)MixerMath.MidiPan(track.Pan)) { Channel = channel });
            chunks.Add(chunk);
        }

        var midiFile = new MidiFile(chunks) { TimeDivision = timeDivision };
        midiFile.ReplaceTempoMap(TempoMap.Create(
            new TicksPerQuarterNoteTimeDivision(TicksPerQuarterNote),
            Tempo.FromBeatsPerMinute(effectiveBpm),
            new TimeSignature(MusicalTime.BeatsPerBar, 4)));
        return midiFile;
    }

    public static void Export(IReadOnlyList<TrackExport> tracks, string filePath, double bpm)
    {
        MidiFile midiFile = BuildMidiFile(tracks, bpm);
        midiFile.Write(filePath, overwriteFile: true, MidiFileFormat.MultiTrack);
    }

    /// <summary>Zero-based MIDI channels: drum tracks 9 (channel 10), the others 0-15 in order,
    /// skipping 9 and starting over after 15.</summary>
    public static int[] AssignChannels(IReadOnlyList<TrackExport> tracks)
    {
        var melodic = Enumerable.Range(0, 16).Where(c => c != MidiImporter.PercussionChannel).ToArray();
        var channels = new int[tracks.Count];
        int next = 0;
        for (int i = 0; i < tracks.Count; i++)
            channels[i] = tracks[i].IsDrums ? MidiImporter.PercussionChannel : melodic[next++ % melodic.Length];
        return channels;
    }
}
