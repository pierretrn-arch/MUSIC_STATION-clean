using System.Collections.ObjectModel;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>One undoable piano-roll edit. <see cref="Do"/> runs again for Redo, so every command
/// reproduces exactly the same notes (same ids) each time it runs - later commands on the redo
/// stack refer to those ids.</summary>
internal interface IEditCommand
{
    void Do();
    void Undo();
}

/// <summary>Moves notes in time and pitch together (dragging a note's body moves the whole
/// selection). Unclamped apart from the floors (start at or after beat 0, pitch 0-127). A note
/// that changes pitch loses its detected confidence and cents.</summary>
internal sealed class MoveNotesCommand : IEditCommand
{
    private readonly ObservableCollection<PianoRollNote> _notes;
    private readonly IReadOnlyList<Guid> _noteIds;
    private readonly double _deltaBeats;
    private readonly int _deltaPitch;
    private List<PianoRollNote>? _before;

    public MoveNotesCommand(ObservableCollection<PianoRollNote> notes, IReadOnlyList<Guid> noteIds, double deltaBeats, int deltaPitch)
    {
        _notes = notes;
        _noteIds = noteIds;
        _deltaBeats = deltaBeats;
        _deltaPitch = deltaPitch;
    }

    public IReadOnlyList<PianoRollNote> Results { get; private set; } = Array.Empty<PianoRollNote>();

    public void Do()
    {
        var before = new List<PianoRollNote>();
        var results = new List<PianoRollNote>();
        foreach (Guid id in _noteIds)
        {
            int index = _notes.IndexOfById(id);
            if (index < 0)
                continue;
            PianoRollNote note = _notes[index];
            before.Add(note);
            int midi = Math.Clamp(note.MidiNumber + _deltaPitch, 0, 127);
            PianoRollNote moved = note with
            {
                StartBeat = Math.Max(0.0, note.StartBeat + _deltaBeats),
                MidiNumber = midi,
                Origin = PianoRollNoteOrigin.Edited,
                // A new pitch: the detection's confidence and cents no longer apply.
                Confidence = midi == note.MidiNumber ? note.Confidence : null,
                CentsDeviation = midi == note.MidiNumber ? note.CentsDeviation : null,
                MeanF0Hz = midi == note.MidiNumber ? note.MeanF0Hz : null,
            };
            _notes[index] = moved;
            results.Add(moved);
        }
        _before = before;
        Results = results;
    }

    public void Undo() => _notes.RestoreById(_before);
}

/// <summary>Resizes a note from either edge - the caller supplies the resulting start/length
/// directly (left-edge drags change both with the end anchored; right-edge drags change only the
/// length). Floors: start at or after 0, length at least <see cref="RollEditing.MinNoteBeats"/>.</summary>
internal sealed class ResizeNoteCommand : IEditCommand
{
    private readonly ObservableCollection<PianoRollNote> _notes;
    private readonly Guid _noteId;
    private readonly double _newStartBeat;
    private readonly double _newDurationBeats;
    private PianoRollNote? _before;

    public ResizeNoteCommand(ObservableCollection<PianoRollNote> notes, Guid noteId, double newStartBeat, double newDurationBeats)
    {
        _notes = notes;
        _noteId = noteId;
        _newStartBeat = newStartBeat;
        _newDurationBeats = newDurationBeats;
    }

    public PianoRollNote? Result { get; private set; }

    public void Do()
    {
        int index = _notes.IndexOfById(_noteId);
        if (index < 0)
            return;

        _before = _notes[index];
        Result = _before with
        {
            StartBeat = Math.Max(0.0, _newStartBeat),
            DurationBeats = Math.Max(RollEditing.MinNoteBeats, _newDurationBeats),
            Origin = PianoRollNoteOrigin.Edited,
        };
        _notes[index] = Result;
    }

    public void Undo() => _notes.RestoreById(_before is null ? null : new[] { _before });
}

/// <summary>Removes some notes and adds others as one step - delete, erase, cut, paste, draw and
/// split are all this. Removed notes go back to their old positions on Undo.</summary>
internal sealed class ReplaceNotesCommand : IEditCommand
{
    private readonly ObservableCollection<PianoRollNote> _notes;
    private readonly IReadOnlyList<Guid> _removeIds;
    private readonly IReadOnlyList<PianoRollNote> _add;
    private List<(int Index, PianoRollNote Note)>? _removed;

    public ReplaceNotesCommand(ObservableCollection<PianoRollNote> notes, IReadOnlyList<Guid> removeIds, IReadOnlyList<PianoRollNote> add)
    {
        _notes = notes;
        _removeIds = removeIds;
        _add = add;
    }

    public IReadOnlyList<PianoRollNote> Added => _add;

    public void Do()
    {
        var removed = new List<(int Index, PianoRollNote Note)>();
        foreach (Guid id in _removeIds.Distinct())
        {
            int index = _notes.IndexOfById(id);
            if (index >= 0)
                removed.Add((index, _notes[index]));
        }

        // Highest-index-first removal so earlier removals don't shift the indices later ones expect.
        removed.Sort((a, b) => b.Index.CompareTo(a.Index));
        foreach ((int index, _) in removed)
            _notes.RemoveAt(index);
        foreach (PianoRollNote note in _add)
            _notes.Add(note);

        _removed = removed;
    }

    public void Undo()
    {
        foreach (PianoRollNote note in _add)
        {
            int index = _notes.IndexOfById(note.Id);
            if (index >= 0)
                _notes.RemoveAt(index);
        }
        if (_removed is null)
            return;
        // Lowest-index-first reinsertion, the reverse order of Do()'s removal.
        foreach ((int index, PianoRollNote note) in _removed.OrderBy(r => r.Index))
            _notes.Insert(Math.Min(index, _notes.Count), note);
    }
}

/// <summary>Independently replaces each of the given notes with a chord voicing rooted on its own
/// pitch. The voices are built once, so Redo brings back the same notes.</summary>
internal sealed class ChordTransformCommand : IEditCommand
{
    private readonly ObservableCollection<PianoRollNote> _notes;
    private readonly IReadOnlyList<Guid> _rootNoteIds;
    private readonly ChordQuality _quality;
    private List<(int Index, PianoRollNote Original, List<PianoRollNote> Voices)>? _transformed;

    public ChordTransformCommand(ObservableCollection<PianoRollNote> notes, IReadOnlyList<Guid> rootNoteIds, ChordQuality quality)
    {
        _notes = notes;
        _rootNoteIds = rootNoteIds;
        _quality = quality;
    }

    public IReadOnlyList<PianoRollNote> ResultVoices { get; private set; } = Array.Empty<PianoRollNote>();

    public void Do()
    {
        if (_transformed is not null)
        {
            Reapply(_transformed);
            return;
        }

        // Highest-index-first: removing/inserting at higher indices first keeps lower,
        // not-yet-processed indices stable.
        var roots = new List<(int Index, PianoRollNote Note)>();
        foreach (Guid id in _rootNoteIds)
        {
            int index = _notes.IndexOfById(id);
            if (index >= 0)
                roots.Add((index, _notes[index]));
        }
        roots.Sort((a, b) => b.Index.CompareTo(a.Index));

        var transformed = new List<(int, PianoRollNote, List<PianoRollNote>)>();
        foreach ((int index, PianoRollNote root) in roots)
        {
            Guid chordGroupId = Guid.NewGuid();
            string chordName = ChordBuilder.ChordName(root.MidiNumber, _quality);
            var voices = ChordBuilder.BuildChord(root.MidiNumber, _quality)
                .Select(midi => root with
                {
                    Id = Guid.NewGuid(),
                    MidiNumber = midi,
                    ChordGroupId = chordGroupId,
                    ChordName = chordName,
                    Origin = PianoRollNoteOrigin.Edited,
                    // Only the root keeps the detection's confidence and cents (same pitch).
                    Confidence = midi == root.MidiNumber ? root.Confidence : null,
                    CentsDeviation = midi == root.MidiNumber ? root.CentsDeviation : null,
                    MeanF0Hz = midi == root.MidiNumber ? root.MeanF0Hz : null,
                })
                .ToList();
            transformed.Add((index, root, voices));
        }

        _transformed = transformed;
        Reapply(transformed);
    }

    private void Reapply(List<(int Index, PianoRollNote Original, List<PianoRollNote> Voices)> transformed)
    {
        var allVoices = new List<PianoRollNote>();
        foreach ((int _, PianoRollNote original, List<PianoRollNote> voices) in transformed)
        {
            int index = _notes.IndexOfById(original.Id);
            if (index < 0)
                continue;
            _notes.RemoveAt(index);
            for (int voice = 0; voice < voices.Count; voice++)
                _notes.Insert(index + voice, voices[voice]);
            allVoices.AddRange(voices);
        }
        ResultVoices = allVoices;
    }

    public void Undo()
    {
        if (_transformed is null)
            return;

        foreach ((int index, PianoRollNote original, List<PianoRollNote> voices) in _transformed.OrderBy(t => t.Index))
        {
            foreach (PianoRollNote voice in voices)
            {
                int voiceIndex = _notes.IndexOfById(voice.Id);
                if (voiceIndex >= 0)
                    _notes.RemoveAt(voiceIndex);
            }
            _notes.Insert(Math.Min(index, _notes.Count), original);
        }
    }
}

internal static class PianoRollNoteCollectionExtensions
{
    public static int IndexOfById(this ObservableCollection<PianoRollNote> notes, Guid id)
    {
        for (int i = 0; i < notes.Count; i++)
        {
            if (notes[i].Id == id)
                return i;
        }
        return -1;
    }

    /// <summary>Puts earlier versions of notes back in place (matched by id).</summary>
    public static void RestoreById(this ObservableCollection<PianoRollNote> notes, IEnumerable<PianoRollNote>? before)
    {
        if (before is null)
            return;
        foreach (PianoRollNote note in before)
        {
            int index = notes.IndexOfById(note.Id);
            if (index >= 0)
                notes[index] = note;
        }
    }
}

/// <summary>An edit made in one track: Undo/Redo select that track so you see what changed.</summary>
internal sealed class TrackScopedCommand : IEditCommand
{
    public TrackScopedCommand(Track track, IEditCommand inner)
    {
        Track = track;
        Inner = inner;
    }

    public Track Track { get; }
    public IEditCommand Inner { get; }

    public void Do() => Inner.Do();
    public void Undo() => Inner.Undo();
}

/// <summary>Adds a track at a position (and removes it again on Undo).</summary>
internal sealed class AddTrackCommand : IEditCommand
{
    private readonly ObservableCollection<Track> _tracks;
    private readonly Track _track;
    private readonly int _index;

    public AddTrackCommand(ObservableCollection<Track> tracks, Track track, int index)
    {
        _tracks = tracks;
        _track = track;
        _index = index;
    }

    public void Do() => _tracks.Insert(Math.Clamp(_index, 0, _tracks.Count), _track);
    public void Undo() => _tracks.Remove(_track);
}

/// <summary>Deletes a track (notes and all); Undo puts it back where it was.</summary>
internal sealed class DeleteTrackCommand : IEditCommand
{
    private readonly ObservableCollection<Track> _tracks;
    private readonly Track _track;
    private int _index;

    public DeleteTrackCommand(ObservableCollection<Track> tracks, Track track)
    {
        _tracks = tracks;
        _track = track;
    }

    public void Do()
    {
        _index = _tracks.IndexOf(_track);
        if (_index >= 0)
            _tracks.RemoveAt(_index);
    }

    public void Undo()
    {
        if (_index >= 0 && !_tracks.Contains(_track))
            _tracks.Insert(Math.Min(_index, _tracks.Count), _track);
    }
}

/// <summary>Changes one track setting (name, colour, instrument) from one value to another.</summary>
internal sealed class TrackSettingCommand<T> : IEditCommand
{
    private readonly Action<T> _set;
    private readonly T _before;
    private readonly T _after;

    public TrackSettingCommand(Action<T> set, T before, T after)
    {
        _set = set;
        _before = before;
        _after = after;
    }

    public void Do() => _set(_after);
    public void Undo() => _set(_before);
}
