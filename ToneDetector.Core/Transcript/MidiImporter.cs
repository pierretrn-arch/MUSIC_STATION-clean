using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

namespace ToneDetector.Core.Transcript;

/// <summary>One track/channel of a MIDI file, offered in the import picker.</summary>
public sealed record MidiPart(string Name, int TrackNumber, int ChannelNumber, bool IsDrums, IReadOnlyList<PianoRollNote> Notes);

/// <summary>Everything File Analysis needs from a MIDI file: its tempo and its parts.</summary>
public sealed record MidiFileContents(double Bpm, IReadOnlyList<MidiPart> Parts);

/// <summary>
/// Reads a Standard MIDI File (.mid/.midi, any format) into parts - one per track and channel -
/// so the user can pick which to load. Notes come in on their beats (ticks / ticks-per-quarter),
/// so they stay on the file's own bars whatever tempo the tab plays at. The tempo is the file's
/// opening tempo, clamped to 40-240 BPM; later tempo changes in the file aren't kept. Channel 10
/// is the General MIDI drum channel, marked so the picker can leave it off by default.
/// </summary>
public static class MidiImporter
{
    /// <summary>Zero-based channel number of General MIDI channel 10 (percussion).</summary>
    public const int PercussionChannel = 9;

    public static MidiFileContents Read(string filePath) => ReadParts(MidiFile.Read(filePath));

    public static MidiFileContents ReadParts(MidiFile midiFile)
    {
        TempoMap tempoMap = midiFile.GetTempoMap();
        double fileBpm = tempoMap.GetTempoAtTime(new MidiTimeSpan(0)).BeatsPerMinute;
        double bpm = MusicalTime.ClampBpm(fileBpm);
        int? ticksPerQuarter = midiFile.TimeDivision is TicksPerQuarterNoteTimeDivision tpq ? tpq.TicksPerQuarterNote : null;

        double ToBeats(long ticks, bool isLength, Note note)
        {
            if (ticksPerQuarter is int tpqn)
                return ticks / (double)tpqn;
            // SMPTE time division: go through real time at the file's opening tempo.
            double ms = isLength
                ? note.LengthAs<MetricTimeSpan>(tempoMap).TotalMilliseconds
                : note.TimeAs<MetricTimeSpan>(tempoMap).TotalMilliseconds;
            return MusicalTime.MsToBeats(ms, fileBpm);
        }

        var parts = new List<MidiPart>();
        var chunks = midiFile.GetTrackChunks().ToList();
        for (int t = 0; t < chunks.Count; t++)
        {
            TrackChunk chunk = chunks[t];
            string? trackName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text?.Trim();
            var byChannel = chunk.GetNotes().GroupBy(n => (int)n.Channel).OrderBy(g => g.Key).ToList();

            foreach (var channelNotes in byChannel)
            {
                int channel = channelNotes.Key;
                bool isDrums = channel == PercussionChannel;
                var notes = channelNotes
                    .Select(n => new PianoRollNote(
                        Guid.NewGuid(), n.NoteNumber,
                        ToBeats(n.Time, false, n),
                        Math.Max(RollEditing.MinNoteBeats, ToBeats(n.Length, true, n)),
                        Velocity: Math.Clamp((int)n.Velocity, 1, 127)))
                    .OrderBy(n => n.StartBeat).ThenBy(n => n.MidiNumber)
                    .ToList();

                parts.Add(new MidiPart(PartName(trackName, t, channel, isDrums, byChannel.Count), t + 1, channel + 1, isDrums, notes));
            }
        }

        return new MidiFileContents(bpm, parts);
    }

    /// <summary>"Piano", "Piano · channel 2" when a track holds several channels, "Track 3" for an
    /// unnamed track, and "Drums (channel 10)" / "Kit · drums (channel 10)" for the percussion channel.</summary>
    public static string PartName(string? trackName, int trackIndex, int channel, bool isDrums, int channelsInTrack)
    {
        bool named = !string.IsNullOrWhiteSpace(trackName);
        if (isDrums)
            return named ? $"{trackName} · drums (channel 10)" : "Drums (channel 10)";
        string name = named ? trackName! : $"Track {trackIndex + 1}";
        return channelsInTrack > 1 ? $"{name} · channel {channel + 1}" : name;
    }
}
