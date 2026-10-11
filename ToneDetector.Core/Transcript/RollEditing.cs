namespace ToneDetector.Core.Transcript;

/// <summary>Pure piano-roll editing rules shared by both tabs: splitting, pasting, marquee
/// selection, the Stop button's target and the default loop region.</summary>
public static class RollEditing
{
    private const double Epsilon = 1e-9;

    /// <summary>Shortest note an edit can leave behind with the grid off.</summary>
    public const double MinNoteBeats = 1.0 / 64.0;

    /// <summary>Splits a note at <paramref name="atBeat"/>: the left part keeps the note's id, the
    /// right part gets a new one; both lose the detection's confidence and cents. Null when the
    /// point isn't inside the note.</summary>
    public static (PianoRollNote Left, PianoRollNote Right)? Split(PianoRollNote note, double atBeat)
    {
        if (atBeat <= note.StartBeat + MinNoteBeats - Epsilon || atBeat >= note.EndBeat - MinNoteBeats + Epsilon)
            return null;

        // The halves are new notes: the detection's confidence and cents described the whole note.
        PianoRollNote left = note with
        {
            DurationBeats = atBeat - note.StartBeat,
            Origin = PianoRollNoteOrigin.Edited,
            Confidence = null,
            CentsDeviation = null,
            MeanF0Hz = null,
        };
        PianoRollNote right = left with
        {
            Id = Guid.NewGuid(),
            StartBeat = atBeat,
            DurationBeats = note.EndBeat - atBeat,
        };
        return (left, right);
    }

    /// <summary>Copies of <paramref name="copied"/> with the earliest one at
    /// <paramref name="positionBeat"/>, pitches and spacing kept. Every copy gets a new id, and
    /// each copied chord a new chord-group id.</summary>
    public static IReadOnlyList<PianoRollNote> PasteAt(IReadOnlyList<PianoRollNote> copied, double positionBeat)
    {
        if (copied.Count == 0)
            return Array.Empty<PianoRollNote>();

        double offset = Math.Max(0.0, positionBeat) - copied.Min(n => n.StartBeat);
        var groups = new Dictionary<Guid, Guid>();
        return copied
            .OrderBy(n => n.StartBeat).ThenBy(n => n.MidiNumber)
            .Select(n => n with
            {
                Id = Guid.NewGuid(),
                StartBeat = Math.Max(0.0, n.StartBeat + offset),
                Origin = PianoRollNoteOrigin.Edited,
                ChordGroupId = n.ChordGroupId is Guid g
                    ? groups.TryGetValue(g, out Guid mapped) ? mapped : groups[g] = Guid.NewGuid()
                    : null,
            })
            .ToList();
    }

    /// <summary>Notes the marquee box touches: overlapping it in time and within its pitch rows.</summary>
    public static IReadOnlyList<Guid> InBox(IEnumerable<PianoRollNote> notes, double beatA, double beatB, int midiA, int midiB)
    {
        double left = Math.Min(beatA, beatB), right = Math.Max(beatA, beatB);
        int low = Math.Min(midiA, midiB), high = Math.Max(midiA, midiB);
        return notes
            .Where(n => n.MidiNumber >= low && n.MidiNumber <= high && n.StartBeat < right && n.EndBeat > left)
            .Select(n => n.Id)
            .ToList();
    }

    /// <summary>Stop: the first press goes to the loop start (Loop on), the next to 0.</summary>
    public static double StopTarget(double positionBeats, bool loopEnabled, double loopStartBeat) =>
        loopEnabled && Math.Abs(positionBeats - loopStartBeat) > Epsilon ? loopStartBeat : 0.0;

    /// <summary>Loop handles before any drag: bar 1 to the end of the last bar with notes (at least one bar).</summary>
    public static (double Start, double End) DefaultLoop(IEnumerable<PianoRollNote> notes)
    {
        double end = EndBeat(notes);
        double bars = Math.Max(1.0, Math.Ceiling(end / MusicalTime.BeatsPerBar - Epsilon));
        return (0.0, bars * MusicalTime.BeatsPerBar);
    }

    public static double EndBeat(IEnumerable<PianoRollNote> notes)
    {
        double end = 0.0;
        foreach (PianoRollNote note in notes)
            end = Math.Max(end, note.EndBeat);
        return end;
    }
}
