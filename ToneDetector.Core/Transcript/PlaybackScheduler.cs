namespace ToneDetector.Core.Transcript;

public enum NoteEventKind
{
    On,
    Off,
}

/// <summary>One note-on or note-off the playback engine should send to the synth.</summary>
public readonly record struct NoteEvent(NoteEventKind Kind, int MidiNumber, int Velocity, Guid NoteId);

/// <summary>
/// Works out which notes start and stop as the playhead moves, in beats - the engine turns wall
/// time into beats with the tempo and calls <see cref="AdvanceTo"/>. Pure and clock-free so the
/// timing rules (loop wrap, notes already sounding at the start point, offs before ons) are
/// testable without audio.
/// </summary>
public sealed class PlaybackScheduler
{
    private const double Epsilon = 1e-9;

    private PianoRollNote[] _notes = Array.Empty<PianoRollNote>();
    private readonly Dictionary<Guid, PianoRollNote> _sounding = new();
    private readonly List<int> _crossedBeats = new();

    /// <summary>Current playhead in beats.</summary>
    public double PositionBeats { get; private set; }

    /// <summary>Loop region, or null when Loop is off.</summary>
    public (double Start, double End)? Loop { get; set; }

    /// <summary>End of the last note.</summary>
    public double EndBeat { get; private set; }

    public bool IsSounding(Guid noteId) => _sounding.ContainsKey(noteId);

    public IReadOnlyCollection<int> SoundingMidiNumbers => _sounding.Values.Select(n => n.MidiNumber).ToList();

    /// <summary>True once the playhead is past the last note and nothing is sounding (never while
    /// looping inside a region).</summary>
    public bool IsFinished => _sounding.Count == 0 && !IsLoopingNow && PositionBeats >= EndBeat - Epsilon;

    private bool IsLoopingNow => Loop is { } loop && loop.End - loop.Start > Epsilon && PositionBeats < loop.End - Epsilon;

    /// <summary>Replaces the notes (e.g. after an edit during playback). Sounding notes that are
    /// gone, or that now end before the playhead, are released; nothing new is triggered mid-note.</summary>
    public IReadOnlyList<NoteEvent> SetNotes(IEnumerable<PianoRollNote> notes)
    {
        _notes = notes.OrderBy(n => n.StartBeat).ToArray();
        EndBeat = _notes.Length == 0 ? 0.0 : _notes.Max(n => n.EndBeat);

        var events = new List<NoteEvent>();
        var byId = _notes.ToDictionary(n => n.Id);
        foreach (PianoRollNote sounding in _sounding.Values.ToList())
        {
            if (!byId.TryGetValue(sounding.Id, out PianoRollNote? updated)
                || updated.MidiNumber != sounding.MidiNumber
                || updated.EndBeat <= PositionBeats + Epsilon)
            {
                events.Add(Off(sounding));
                _sounding.Remove(sounding.Id);
            }
            else
            {
                _sounding[sounding.Id] = updated;
            }
        }
        return events;
    }

    /// <summary>Starts from <paramref name="startBeat"/>: notes already sounding there are triggered.</summary>
    public IReadOnlyList<NoteEvent> Start(double startBeat)
    {
        var events = new List<NoteEvent>(ReleaseAll());
        _crossedBeats.Clear();
        PositionBeats = Math.Max(0.0, startBeat);
        MarkBeatIfWhole(PositionBeats);
        TriggerSpanning(PositionBeats, events);
        return events;
    }

    /// <summary>The whole beats reached since the last call, in order (for the metronome): the
    /// start beat if playback starts on one, every beat passed, and the loop start after a wrap
    /// (the loop end itself isn't a click - playback is already back at the start).</summary>
    public IReadOnlyList<int> DrainCrossedBeats()
    {
        if (_crossedBeats.Count == 0)
            return Array.Empty<int>();
        var beats = _crossedBeats.ToList();
        _crossedBeats.Clear();
        return beats;
    }

    private void MarkBeatIfWhole(double beat)
    {
        double rounded = Math.Round(beat);
        if (Math.Abs(beat - rounded) < 1e-6)
            _crossedBeats.Add((int)rounded);
    }

    private void MarkBeatsBetween(double from, double to, bool includeEnd)
    {
        int first = (int)Math.Floor(from + 1e-6) + 1;
        int last = includeEnd ? (int)Math.Floor(to + 1e-6) : (int)Math.Ceiling(to - 1e-6) - 1;
        for (int beat = first; beat <= last; beat++)
            _crossedBeats.Add(beat);
    }

    /// <summary>Moves the playhead forward, wrapping at the loop end.</summary>
    public IReadOnlyList<NoteEvent> AdvanceTo(double targetBeat)
    {
        var events = new List<NoteEvent>();
        double remaining = targetBeat - PositionBeats;
        int guard = 0;
        while (remaining > Epsilon && guard++ < 1000)
        {
            if (Loop is { } loop && loop.End - loop.Start > Epsilon && PositionBeats < loop.End - Epsilon
                && PositionBeats + remaining >= loop.End - Epsilon)
            {
                remaining -= loop.End - PositionBeats;
                MarkBeatsBetween(PositionBeats, loop.End, includeEnd: false);
                Step(loop.End, events, includeStartsAtTarget: false);
                events.AddRange(ReleaseAll());
                PositionBeats = loop.Start;
                MarkBeatIfWhole(PositionBeats);
                TriggerSpanning(PositionBeats, events);
                continue;
            }

            MarkBeatsBetween(PositionBeats, PositionBeats + remaining, includeEnd: true);
            Step(PositionBeats + remaining, events, includeStartsAtTarget: true);
            remaining = 0;
        }
        return events;
    }

    /// <summary>Note-offs for everything sounding (pause/stop).</summary>
    public IReadOnlyList<NoteEvent> ReleaseAll()
    {
        var events = _sounding.Values.Select(Off).ToList();
        _sounding.Clear();
        return events;
    }

    /// <summary>Processes (Position, target]: offs for notes ending by then, ons for notes starting
    /// in it, in time order with offs first at the same instant.</summary>
    private void Step(double target, List<NoteEvent> events, bool includeStartsAtTarget)
    {
        double from = PositionBeats;
        var timed = new List<(double Time, int Order, NoteEvent Event, PianoRollNote Note)>();

        foreach (PianoRollNote note in _notes)
        {
            if (note.StartBeat > target + Epsilon)
                break;
            bool startsInWindow = note.StartBeat > from + Epsilon
                && (includeStartsAtTarget ? note.StartBeat <= target + Epsilon : note.StartBeat < target - Epsilon);
            if (startsInWindow && !_sounding.ContainsKey(note.Id))
                timed.Add((note.StartBeat, 1, On(note), note));
        }

        foreach (PianoRollNote note in _sounding.Values)
        {
            if (note.EndBeat <= target + Epsilon)
                timed.Add((note.EndBeat, 0, Off(note), note));
        }

        foreach ((double _, int _, NoteEvent evt, PianoRollNote note) in timed.OrderBy(t => t.Time).ThenBy(t => t.Order))
        {
            if (evt.Kind == NoteEventKind.On)
            {
                // A note that also ends inside this step is released on the next step.
                _sounding[note.Id] = note;
                events.Add(evt);
            }
            else if (_sounding.Remove(note.Id))
            {
                events.Add(evt);
            }
        }

        PositionBeats = target;
    }

    private void TriggerSpanning(double beat, List<NoteEvent> events)
    {
        foreach (PianoRollNote note in _notes)
        {
            if (note.StartBeat > beat + Epsilon)
                break;
            if (note.EndBeat > beat + Epsilon && _sounding.TryAdd(note.Id, note))
                events.Add(On(note));
        }
    }

    private static NoteEvent On(PianoRollNote note) => new(NoteEventKind.On, note.MidiNumber, note.Velocity, note.Id);

    private static NoteEvent Off(PianoRollNote note) => new(NoteEventKind.Off, note.MidiNumber, note.Velocity, note.Id);
}
