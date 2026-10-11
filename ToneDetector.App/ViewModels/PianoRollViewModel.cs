using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>The piano roll's edit tools (one active at a time).</summary>
public enum EditTool
{
    Select,
    Draw,
    Erase,
    Split,
}

/// <summary>One entry of the Grid menu.</summary>
public sealed record GridOption(GridDivision Division, string Label);

/// <summary>Copied notes, shared by both tabs so you can copy in one and paste in the other.</summary>
internal static class RollClipboard
{
    public static IReadOnlyList<PianoRollNote> Notes { get; set; } = Array.Empty<PianoRollNote>();
}

/// <summary>
/// One tab's piano roll: its tracks (each with notes in beats), tempo, grid, edit tool, loop
/// region, playhead, and the edit history behind Undo/Redo. Both tabs have one. The roll edits the
/// <see cref="SelectedTrack"/> (<see cref="RollNotes"/> is its notes); the other tracks show as
/// ghost notes. The Live Tuner's mirrors the live transcript into its take track (see
/// <see cref="OnTranscriptCollectionChanged"/>); File Analysis loads a file's parts with
/// <see cref="ReplaceTracks"/>. Nothing here clamps against a neighbour - notes may freely
/// overlap at any pitch/time (including the same pitch, a deliberate retrigger case).
/// </summary>
public sealed class PianoRollViewModel : INotifyPropertyChanged
{
    private readonly PlaybackViewModel _playback;
    private readonly LiveTunerViewModel? _liveTuner;

    /// <summary>Every edit pushes its command here after <c>Do()</c>; Undo pops one onto
    /// <see cref="_redoStack"/>, and any new edit clears Redo.</summary>
    private readonly Stack<IEditCommand> _undoStack = new();
    private readonly Stack<IEditCommand> _redoStack = new();

    private bool _hasCustomLoop;

    /// <summary>"Take" (Live Tuner) or "Track" (File Analysis): the first track's name.</summary>
    private readonly string _firstTrackPrefix;

    /// <summary>How many tracks this tab has made since the last load/Clear: picks the next colour.</summary>
    private int _tracksCreated;

    /// <summary>The track the Live Tuner's transcript goes into.</summary>
    private Track? _liveTrack;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Any track's notes changed, a track was added/removed, or mute/solo changed: what
    /// playback should hear may be different.</summary>
    public event EventHandler? PlaybackNotesChanged;

    /// <summary>A track's volume or pan changed (playback updates its mixer live).</summary>
    public event EventHandler? MixChanged;

    /// <summary>The selected track's notes changed, or another track was selected.</summary>
    public event EventHandler? SelectedNotesChanged;

    /// <summary>Something the roll draws for the other tracks changed: their notes, colours, or
    /// which tracks there are.</summary>
    public event EventHandler? GhostsChanged;

    /// <summary>Every track was replaced (a load or Clear): the roll goes back to the start and
    /// scrolls to the new notes.</summary>
    public event EventHandler? TracksReplaced;

    /// <summary>This tab's tracks, top to bottom in the tracks panel.</summary>
    public ObservableCollection<Track> Tracks { get; } = new();

    private Track _selectedTrack = null!;
    /// <summary>The track the roll edits (never null; there is always at least one track).</summary>
    public Track SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedTrack) || !Tracks.Contains(value))
                return;
            if (_selectedTrack is not null)
                _selectedTrack.IsSelected = false;
            _selectedTrack = value;
            value.IsSelected = true;
            _lastSelectedIndexHint = Tracks.IndexOf(value);
            OnPropertyChanged(nameof(SelectedTrack));
            OnPropertyChanged(nameof(RollNotes));
            OnSelectedNotesChanged();
            GhostsChanged?.Invoke(this, EventArgs.Empty);
            RefreshSounding();
        }
    }

    /// <summary>The selected track's notes: what the roll draws, edits and selects.</summary>
    public ObservableCollection<PianoRollNote> RollNotes => SelectedTrack.Notes;

    /// <summary>Every track's notes.</summary>
    public IEnumerable<PianoRollNote> AllNotes => Tracks.SelectMany(t => t.Notes);

    public bool HasAnyNotes => Tracks.Any(t => t.Notes.Count > 0);

    /// <summary>True if any track is soloed (then only soloed tracks play).</summary>
    public bool AnySolo => Tracks.Any(t => t.IsSolo);

    /// <summary>Whether a track is heard: not muted, and soloed if any track is.</summary>
    public bool IsAudible(Track track) => MixerMath.IsAudible(track.IsMuted, track.IsSolo, AnySolo);

    /// <summary>Set by the view: shows a Yes/No question and returns true for Yes. Without it,
    /// every question is answered Yes (tests).</summary>
    public Func<string, bool>? Confirm { get; set; }

    internal const string ReplaceTracksQuestion = "Replace your current tracks? They aren't saved.";

    /// <summary>Asks before replacing tracks that have notes; true to go ahead.</summary>
    public bool ConfirmReplaceTracks() => !HasAnyNotes || Confirm?.Invoke(ReplaceTracksQuestion) != false;

    /// <summary>The selected track as notation input: one line of notes and rests (a note that
    /// starts under an earlier one is left out - notation is one voice).</summary>
    public ObservableCollection<TranscriptEntryViewModel> NotationEntries { get; } = new();

    private double _pixelsPerBeat = DefaultPixelsPerBeat;
    public const double DefaultPixelsPerBeat = 48.0;
    public const double MinPixelsPerBeat = 6.0;
    public const double MaxPixelsPerBeat = 400.0;

    /// <summary>Time zoom (Alt+wheel), remembered per tab in settings.</summary>
    public double PixelsPerBeat
    {
        get => _pixelsPerBeat;
        set => SetField(ref _pixelsPerBeat, ClampZoom(value));
    }

    public static double ClampZoom(double pixelsPerBeat) =>
        double.IsFinite(pixelsPerBeat) ? Math.Clamp(pixelsPerBeat, MinPixelsPerBeat, MaxPixelsPerBeat) : DefaultPixelsPerBeat;

    public static IReadOnlyList<GridOption> GridOptions { get; } =
        GridDivisions.All.Select(d => new GridOption(d, GridDivisions.Label(d))).ToList();

    public RelayCommand AddTrackCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand UndoCommand { get; }
    public RelayCommand RedoCommand { get; }
    public RelayCommand PlayPauseCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand RewindCommand { get; }
    public RelayCommand IncreaseTempoCommand { get; }
    public RelayCommand DecreaseTempoCommand { get; }

    /// <summary>Live Tuner Record: with the metronome on, a 1-bar count-in first; the take starts
    /// at the playhead.</summary>
    public RelayCommand RecordCommand { get; }

    /// <summary>Stops recording, or cancels the count-in.</summary>
    public RelayCommand StopRecordingCommand { get; }

    public bool CanUndoEdit => _undoStack.Count > 0;
    public bool CanRedoEdit => _redoStack.Count > 0;

    /// <summary>A roll for File Analysis (notes come from <see cref="ReplaceAll"/>).</summary>
    public PianoRollViewModel(PlaybackViewModel playback) : this(playback, null)
    {
    }

    /// <summary>A roll that mirrors the Live Tuner's transcript.</summary>
    public PianoRollViewModel(PlaybackViewModel playback, LiveTunerViewModel? liveTuner)
    {
        _playback = playback;
        _liveTuner = liveTuner;
        _firstTrackPrefix = liveTuner is null ? "Track" : "Take";
        _playback.SoundingChanged += (_, _) => RefreshSounding();
        Tracks.CollectionChanged += OnTracksChanged;
        ResetTracksCore(Array.Empty<TrackSeed>());

        AddTrackCommand = new RelayCommand(_ => AddTrack(), _ => !IsRecording && !IsCountingIn);
        ClearCommand = new RelayCommand(_ => Clear(), _ => !IsRecording && !IsCountingIn);

        UndoCommand = new RelayCommand(_ => Undo(), _ => CanUndoEdit && !IsRecording);
        RedoCommand = new RelayCommand(_ => Redo(), _ => CanRedoEdit && !IsRecording);
        PlayPauseCommand = new RelayCommand(_ => PlayOrPause(), _ => !IsRecording && !IsCountingIn && (IsPlaying || HasAnyNotes));
        RecordCommand = new RelayCommand(_ => Record(), _ => _liveTuner is { IsPipelineRunning: true, IsRecording: false } && !IsCountingIn);
        StopRecordingCommand = new RelayCommand(_ => StopRecording(), _ => IsRecording || IsCountingIn);
        StopCommand = new RelayCommand(_ => Stop());
        RewindCommand = new RelayCommand(_ => SeekTo(0));
        IncreaseTempoCommand = new RelayCommand(_ => Bpm += 1, _ => Bpm < MusicalTime.MaxBpm);
        DecreaseTempoCommand = new RelayCommand(_ => Bpm -= 1, _ => Bpm > MusicalTime.MinBpm);

        if (_liveTuner is not null)
        {
            _liveTuner.Transcript.CollectionChanged += OnTranscriptCollectionChanged;
            _liveTuner.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(LiveTunerViewModel.IsRecording))
                    return;
                if (_liveTuner.IsRecording)
                    OnTakeStarted();
                else if (ReferenceEquals(_playback.ActiveRoll, this) && _playback.IsRecordingClock)
                    _playback.StopPlayback(); // recording ended some other way: stop the clock too
                OnPropertyChanged(nameof(IsRecordingTake));
                RaiseEditStateChanged();
            };
            ResyncFromTranscript();
        }
    }

    private bool IsRecording => _liveTuner?.IsRecording == true;

    /// <summary>True while the Live Tuner records into this roll.</summary>
    public bool IsRecordingTake => IsRecording;

    // ---- Recording and metronome (Live Tuner) ----

    private bool _isMetronomeOn;
    /// <summary>Clicks on each beat during playback and recording; with it on, Record starts
    /// after a 1-bar count-in. Session-only.</summary>
    public bool IsMetronomeOn
    {
        get => _isMetronomeOn;
        set => SetField(ref _isMetronomeOn, value);
    }

    private bool _isCountingIn;
    /// <summary>True during the count-in bar before recording starts.</summary>
    public bool IsCountingIn
    {
        get => _isCountingIn;
        private set
        {
            if (SetField(ref _isCountingIn, value))
                RaiseEditStateChanged();
        }
    }

    // Where the current take goes: the beat recording started on, and whether Record (rather
    // than something else) started it.
    private double _takeStartBeat;
    private bool _takeStartSet;

    /// <summary>The beat the latest take started on (the Live Tuner scrolls back to it after recording).</summary>
    public double LastTakeStartBeat => _takeStartBeat;

    private void Record()
    {
        if (_liveTuner is null)
            return;
        _liveTrack = SelectedTrack;
        IsCountingIn = IsMetronomeOn;
        _playback.StartRecording(this, IsMetronomeOn ? MusicalTime.BeatsPerBar : 0, beat =>
        {
            IsCountingIn = false;
            _takeStartBeat = Math.Max(0.0, beat);
            _takeStartSet = true;
            _liveTuner.IsRecording = true;
        });
    }

    private void StopRecording()
    {
        IsCountingIn = false;
        if (_liveTuner is { IsRecording: true })
            _liveTuner.IsRecording = false;
        if (ReferenceEquals(_playback.ActiveRoll, this) && _playback.IsRecordingClock)
            _playback.StopPlayback();
    }

    /// <summary>A take started without Record (e.g. the Live Tuner's own Start) goes at the playhead.</summary>
    private void OnTakeStarted()
    {
        if (!_takeStartSet)
            _takeStartBeat = PositionBeats;
        _takeStartSet = false;
    }

    /// <summary>Where a recorded note lands: the take's start beat plus how long after the take
    /// started it was sung, moved earlier by the output latency (you sing with the click you hear).</summary>
    private double TakeBeatFor(TranscriptEntryViewModel entry)
    {
        double msIntoTake = entry.StartTimeMs - _liveTuner!.TakeStartTranscriptMs - _playback.OutputLatencyMs;
        return Math.Max(0.0, _takeStartBeat + MusicalTime.MsToBeats(msIntoTake, Bpm));
    }

    // ---- Tempo, grid, tool ----

    private double _bpm = MusicalTime.DefaultBpm;
    /// <summary>Tempo, a whole number 40-240. Notes stay on their beats when it changes.</summary>
    public double Bpm
    {
        get => _bpm;
        set
        {
            if (SetField(ref _bpm, MusicalTime.ClampBpm(value)))
            {
                OnPropertyChanged(nameof(MsPerBeat));
                RebuildNotation();
            }
        }
    }

    public double MsPerBeat => MusicalTime.MsPerBeat(Bpm);

    private GridDivision _grid = GridDivisions.Default;
    public GridDivision Grid
    {
        get => _grid;
        set => SetField(ref _grid, value);
    }

    private EditTool _tool = EditTool.Select;
    public EditTool Tool
    {
        get => _tool;
        set
        {
            if (!SetField(ref _tool, value))
                return;
            OnPropertyChanged(nameof(IsSelectTool));
            OnPropertyChanged(nameof(IsDrawTool));
            OnPropertyChanged(nameof(IsEraseTool));
            OnPropertyChanged(nameof(IsSplitTool));
        }
    }

    // Two-way bindings for the tool toggle group.
    public bool IsSelectTool { get => Tool == EditTool.Select; set { if (value) Tool = EditTool.Select; } }
    public bool IsDrawTool { get => Tool == EditTool.Draw; set { if (value) Tool = EditTool.Draw; } }
    public bool IsEraseTool { get => Tool == EditTool.Erase; set { if (value) Tool = EditTool.Erase; } }
    public bool IsSplitTool { get => Tool == EditTool.Split; set { if (value) Tool = EditTool.Split; } }

    // ---- Transport ----

    private bool _isLoopEnabled;
    /// <summary>Session-only, per tab.</summary>
    public bool IsLoopEnabled
    {
        get => _isLoopEnabled;
        set => SetField(ref _isLoopEnabled, value);
    }

    private double _loopStartBeat;
    public double LoopStartBeat
    {
        get => _loopStartBeat;
        private set => SetField(ref _loopStartBeat, value);
    }

    private double _loopEndBeat = MusicalTime.BeatsPerBar;
    public double LoopEndBeat
    {
        get => _loopEndBeat;
        private set => SetField(ref _loopEndBeat, value);
    }

    /// <summary>Sets the loop region from the S/E handles (snapped to beats by the caller). Keeps
    /// at least one beat between them; from then on the region no longer follows the notes.</summary>
    public void SetLoop(double startBeat, double endBeat)
    {
        double start = Math.Max(0.0, Math.Min(startBeat, endBeat));
        double end = Math.Max(start + 1.0, Math.Max(startBeat, endBeat));
        _hasCustomLoop = true;
        LoopStartBeat = start;
        LoopEndBeat = end;
    }

    private double _positionBeats;
    /// <summary>The playhead / time position: where Play starts and Paste goes.</summary>
    public double PositionBeats
    {
        get => _positionBeats;
        set
        {
            if (SetField(ref _positionBeats, Math.Max(0.0, value)))
            {
                OnPropertyChanged(nameof(PositionText));
                OnPropertyChanged(nameof(EndText));
            }
        }
    }

    /// <summary>"3.2.1".</summary>
    public string PositionText => MusicalTime.FormatPosition(PositionBeats);

    /// <summary>The total, "5.3.1": the end of the last note, or the playhead if that's further
    /// (so it never reads less than the position, e.g. while recording an empty take).</summary>
    public string EndText => MusicalTime.FormatPosition(Math.Max(RollEditing.EndBeat(AllNotes), PositionBeats));

    private bool _isPlaying;
    /// <summary>Set by the playback engine while this tab plays.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        internal set
        {
            if (SetField(ref _isPlaying, value))
                RaiseEditStateChanged();
        }
    }

    /// <summary>Moves the playhead (a click on the ruler or empty grid); keeps playing from there.</summary>
    public void SeekTo(double beat)
    {
        PositionBeats = beat;
        if (IsPlaying)
            _playback.Play(this);
    }

    private void PlayOrPause()
    {
        if (IsPlaying)
            _playback.StopPlayback();
        else
            _playback.Play(this);
    }

    /// <summary>First press: loop start (Loop on) or 0; second press: 0.</summary>
    public void Stop()
    {
        if (IsPlaying)
            _playback.StopPlayback();
        PositionBeats = RollEditing.StopTarget(PositionBeats, IsLoopEnabled, LoopStartBeat);
    }

    // ---- Sounding keys and notes (key column and outlines) ----

    public IReadOnlyCollection<int> SoundingKeys { get; private set; } = Array.Empty<int>();
    public IReadOnlyCollection<Guid> SoundingNoteIds { get; private set; } = Array.Empty<Guid>();

    /// <summary>Keys light up for held keys and for the selected track's sounding notes.</summary>
    private void RefreshSounding()
    {
        bool active = ReferenceEquals(_playback.ActiveRoll, this);
        SoundingNoteIds = active ? _playback.SoundingNoteIds : Array.Empty<Guid>();
        if (active && SoundingNoteIds.Count > 0)
        {
            var ids = SoundingNoteIds as ISet<Guid> ?? new HashSet<Guid>(SoundingNoteIds);
            SoundingKeys = _playback.HeldKeys
                .Union(RollNotes.Where(n => ids.Contains(n.Id)).Select(n => n.MidiNumber))
                .ToArray();
        }
        else
        {
            SoundingKeys = _playback.HeldKeys.ToArray();
        }
        OnPropertyChanged(nameof(SoundingKeys));
        OnPropertyChanged(nameof(SoundingNoteIds));
    }

    // ---- Tracks ----

    /// <summary>Replaces every track (a newly loaded file, or Clear): clears the edit history, the
    /// loop region goes back to following the notes, and the playhead to the start. Colours start
    /// again from track-1. No seeds gives one empty track.</summary>
    public void ReplaceTracks(IReadOnlyList<TrackSeed> seeds)
    {
        if (IsPlaying)
            _playback.StopPlayback();
        ResetTracksCore(seeds);
        _undoStack.Clear();
        _redoStack.Clear();
        _hasCustomLoop = false;
        UpdateDefaultLoop();
        PositionBeats = 0;
        RaiseEditStateChanged();
        TracksReplaced?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>One track holding <paramref name="notes"/> (see <see cref="ReplaceTracks"/>).</summary>
    public void ReplaceAll(IEnumerable<PianoRollNote> notes) =>
        ReplaceTracks(new[] { new TrackSeed(FirstTrackName, notes.ToList()) });

    private string FirstTrackName => $"{_firstTrackPrefix} 1";

    private void ResetTracksCore(IReadOnlyList<TrackSeed> seeds)
    {
        Tracks.Clear();
        _tracksCreated = 0;
        IEnumerable<TrackSeed> all = seeds.Count > 0 ? seeds : new[] { new TrackSeed(FirstTrackName, Array.Empty<PianoRollNote>()) };
        foreach (TrackSeed seed in all)
        {
            Track track = NewTrack(seed.Name, seed.IsDrums);
            foreach (PianoRollNote note in seed.Notes)
                track.Notes.Add(note);
            Tracks.Add(track);
        }
        _liveTrack = Tracks[0];
        _selectedTrack = null!;
        SelectedTrack = Tracks[0];
    }

    private Track NewTrack(string name, bool isDrums = false) =>
        new(name, _tracksCreated++, isDrums) { Instrument = _instrumentName };

    private string _instrumentName = "Built-in synth";
    /// <summary>The Settings synth engine's name: what every track plays through until per-track
    /// instruments (Phase 6b Stage 2). Shown in each track's instrument picker and written to exports.</summary>
    public string InstrumentName
    {
        get => _instrumentName;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || !SetField(ref _instrumentName, value))
                return;
            foreach (Track track in Tracks)
                track.Instrument = value;
        }
    }

    /// <summary>"+ Add track": a new empty track at the bottom, selected. Undoable.</summary>
    public Track AddTrack()
    {
        Track track = NewTrack($"Track {Tracks.Count + 1}");
        var command = new TrackScopedCommand(track, new AddTrackCommand(Tracks, track, Tracks.Count));
        command.Do();
        SelectedTrack = track;
        Push(command);
        return track;
    }

    /// <summary>Deletes a track and its notes (not the last one). Undoable.</summary>
    public bool DeleteTrack(Track track)
    {
        if (Tracks.Count <= 1 || !Tracks.Contains(track) || IsRecording)
            return false;
        int index = Tracks.IndexOf(track);
        var command = new TrackScopedCommand(track, new DeleteTrackCommand(Tracks, track));
        command.Do();
        if (ReferenceEquals(SelectedTrack, track))
            SelectedTrack = Tracks[Math.Min(index, Tracks.Count - 1)];
        Push(command);
        return true;
    }

    public bool CanDeleteTrack => Tracks.Count > 1 && !IsRecording;

    /// <summary>Renames a track (blank names are ignored). Undoable.</summary>
    public void RenameTrack(Track track, string name)
    {
        string trimmed = (name ?? "").Trim();
        if (trimmed.Length == 0 || trimmed == track.Name)
            return;
        PushTrackSetting(track, v => track.Name = v, track.Name, trimmed);
    }

    /// <summary>Gives a track another palette colour. Undoable.</summary>
    public void SetTrackColour(Track track, int colourIndex)
    {
        int index = TrackPalette.IndexFor(colourIndex);
        if (index == track.ColourIndex)
            return;
        PushTrackSetting(track, v => track.ColourIndex = v, track.ColourIndex, index);
    }

    /// <summary>Changes what a track plays through. Undoable.</summary>
    public void SetTrackInstrument(Track track, string instrument)
    {
        if (string.IsNullOrWhiteSpace(instrument) || instrument == track.Instrument)
            return;
        PushTrackSetting(track, v => track.Instrument = v, track.Instrument, instrument);
    }

    private void PushTrackSetting<T>(Track track, Action<T> set, T before, T after)
    {
        var command = new TrackScopedCommand(track, new TrackSettingCommand<T>(set, before, after));
        command.Do();
        Push(command);
    }

    /// <summary>Live Tuner Clear: asks first if any track has notes, then clears the transcript and
    /// goes back to one empty "Take 1".</summary>
    private void Clear()
    {
        if (!ConfirmReplaceTracks())
            return;
        if (_liveTuner is not null && _liveTuner.ClearCommand.CanExecute(null))
            _liveTuner.ClearCommand.Execute(null);
        ReplaceTracks(Array.Empty<TrackSeed>());
    }

    private void OnTracksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (Track track in _subscribed)
                Unsubscribe(track);
            _subscribed.Clear();
        }
        if (e.OldItems is not null)
        {
            foreach (Track track in e.OldItems)
            {
                Unsubscribe(track);
                _subscribed.Remove(track);
            }
        }
        foreach (Track track in Tracks)
        {
            if (_subscribed.Add(track))
            {
                track.Notes.CollectionChanged += OnTrackNotesChanged;
                track.PropertyChanged += OnTrackPropertyChanged;
            }
        }
        OnPropertyChanged(nameof(CanDeleteTrack));
        OnAnyNotesChanged();
        GhostsChanged?.Invoke(this, EventArgs.Empty);
    }

    private readonly HashSet<Track> _subscribed = new();

    private void Unsubscribe(Track track)
    {
        track.Notes.CollectionChanged -= OnTrackNotesChanged;
        track.PropertyChanged -= OnTrackPropertyChanged;
    }

    private void OnTrackNotesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnAnyNotesChanged();
        if (ReferenceEquals(sender, SelectedTrack.Notes))
            OnSelectedNotesChanged();
        else
            GhostsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(Track.IsMuted):
            case nameof(Track.IsSolo):
                OnPropertyChanged(nameof(AnySolo));
                PlaybackNotesChanged?.Invoke(this, EventArgs.Empty);
                break;
            case nameof(Track.Volume):
            case nameof(Track.Pan):
                MixChanged?.Invoke(this, EventArgs.Empty);
                break;
            case nameof(Track.ColourIndex):
                GhostsChanged?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void OnAnyNotesChanged()
    {
        UpdateDefaultLoop();
        OnPropertyChanged(nameof(EndText));
        OnPropertyChanged(nameof(HasAnyNotes));
        PlaybackNotesChanged?.Invoke(this, EventArgs.Empty);
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void OnSelectedNotesChanged()
    {
        RebuildNotation();
        SelectedNotesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildNotation()
    {
        NotationEntries.Clear();
        double cursorBeat = 0.0;
        double msPerBeat = MsPerBeat;
        foreach (PianoRollNote note in RollNotes.OrderBy(n => n.StartBeat).ThenByDescending(n => n.MidiNumber))
        {
            if (note.StartBeat < cursorBeat - 1e-6)
                continue; // under an earlier note: one voice only
            double gapBeats = note.StartBeat - cursorBeat;
            if (gapBeats > 1e-6)
            {
                NotationEntries.Add(new TranscriptEntryViewModel
                {
                    Type = "Silence",
                    StartTimeMs = cursorBeat * msPerBeat,
                    DurationMs = gapBeats * msPerBeat,
                });
            }
            (string name, int octave) = ToneDetector.Core.Pitch.NoteQuantizer.GetNoteNameAndOctave(note.MidiNumber);
            NotationEntries.Add(new TranscriptEntryViewModel
            {
                Type = "Note",
                Note = name,
                Octave = octave,
                MidiNumber = note.MidiNumber,
                StartTimeMs = note.StartBeat * msPerBeat,
                DurationMs = note.DurationBeats * msPerBeat,
            });
            cursorBeat = note.EndBeat;
        }
    }

    private void UpdateDefaultLoop()
    {
        if (_hasCustomLoop)
            return;
        (double start, double end) = RollEditing.DefaultLoop(AllNotes);
        LoopStartBeat = start;
        LoopEndBeat = end;
    }

    /// <summary>The notes playback should hear: every audible track's notes, with their track.</summary>
    public IReadOnlyList<(PianoRollNote Note, Track Track)> AudibleNotes()
    {
        bool anySolo = AnySolo;
        var notes = new List<(PianoRollNote, Track)>();
        foreach (Track track in Tracks)
        {
            if (!MixerMath.IsAudible(track.IsMuted, track.IsSolo, anySolo))
                continue;
            foreach (PianoRollNote note in track.Notes)
                notes.Add((note, track));
        }
        return notes;
    }

    /// <summary>Every track, for export (mute/solo are written but don't leave anything out).</summary>
    public IReadOnlyList<TrackExport> ExportTracks() => Tracks.Select(t => t.ToExport()).ToList();

    /// <summary>The live mirror. <see cref="TranscriptEntryViewModel"/> has no stable identity, so
    /// there's no way to patch just one changed row in place:
    /// <list type="bullet">
    /// <item>While recording, the only source of an <c>Add</c> is newly committed notes from the
    /// live pipeline - appended, so edits made before this recording pass are left alone.</item>
    /// <item>While stopped, any change fully re-syncs so the transcript and roll never drift apart
    /// (this discards roll-only edits; typed Grid editing is deferred, so in practice only Clear
    /// changes the transcript while stopped).</item>
    /// </list>
    /// A recorded note lands at the take's start beat plus how far into the take it was sung
    /// (<see cref="TakeBeatFor"/>); a full resync places notes by their transcript time.</summary>
    private void OnTranscriptCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            ResyncFromTranscript();
            return;
        }

        if (IsRecording)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null)
            {
                Track target = LiveTrack;
                foreach (TranscriptEntryViewModel entry in e.NewItems)
                {
                    if (entry.Type == "Note")
                        target.Notes.Add(ToRollNote(entry) with { StartBeat = TakeBeatFor(entry) });
                }
            }
            return;
        }

        ResyncFromTranscript();
    }

    /// <summary>The take track (the one recording went into), or the selected track if that one
    /// was deleted.</summary>
    private Track LiveTrack => _liveTrack is not null && Tracks.Contains(_liveTrack) ? _liveTrack : SelectedTrack;

    /// <summary>Full re-derive of the take track from the transcript's <c>Type == "Note"</c> rows;
    /// also clears the edit history, since note ids a pending command refers to may be gone. An
    /// empty transcript (Clear) leaves the other tracks alone - the roll's own Clear resets them.</summary>
    private void ResyncFromTranscript()
    {
        if (_liveTuner is null)
            return;
        Track target = LiveTrack;
        target.Notes.Clear();
        foreach (TranscriptEntryViewModel entry in _liveTuner.Transcript)
        {
            if (entry.Type == "Note")
                target.Notes.Add(ToRollNote(entry));
        }

        _undoStack.Clear();
        _redoStack.Clear();
        RaiseEditStateChanged();
    }

    private PianoRollNote ToRollNote(TranscriptEntryViewModel entry) => new(
        Guid.NewGuid(), entry.MidiNumber,
        MusicalTime.MsToBeats(entry.StartTimeMs, Bpm), MusicalTime.MsToBeats(entry.DurationMs, Bpm),
        Velocity: 100, Origin: PianoRollNoteOrigin.SeededFromTranscript, ChordGroupId: entry.ChordGroupId,
        Confidence: entry.ConfidenceMean, CentsDeviation: entry.CentsDeviation, MeanF0Hz: entry.MeanF0Hz);

    // ---- Edits (each is one Undo step) ----

    /// <summary>Moves notes in time and pitch together (dragging a body moves the selection).</summary>
    public IReadOnlyList<PianoRollNote> MoveNotes(IReadOnlyList<Guid> noteIds, double deltaBeats, int deltaPitch)
    {
        if (noteIds.Count == 0 || (Math.Abs(deltaBeats) < 1e-9 && deltaPitch == 0))
            return Array.Empty<PianoRollNote>();
        var command = new MoveNotesCommand(RollNotes, noteIds, deltaBeats, deltaPitch);
        command.Do();
        if (command.Results.Count == 0)
            return Array.Empty<PianoRollNote>();
        Push(new TrackScopedCommand(SelectedTrack, command));
        return command.Results;
    }

    /// <summary>Moves one note to a new start and pitch.</summary>
    public PianoRollNote? MoveNote(Guid noteId, double newStartBeat, int newMidiNumber)
    {
        PianoRollNote? note = RollNotes.FirstOrDefault(n => n.Id == noteId);
        if (note is null)
            return null;
        IReadOnlyList<PianoRollNote> moved = MoveNotes(new[] { noteId }, newStartBeat - note.StartBeat, newMidiNumber - note.MidiNumber);
        return moved.Count > 0 ? moved[0] : note;
    }

    /// <summary>Resizes from either edge; the caller supplies the resulting start/length.</summary>
    public PianoRollNote? ResizeNote(Guid noteId, double newStartBeat, double newDurationBeats)
    {
        var command = new ResizeNoteCommand(RollNotes, noteId, newStartBeat, newDurationBeats);
        command.Do();
        if (command.Result is null)
            return null;
        Push(new TrackScopedCommand(SelectedTrack, command));
        return command.Result;
    }

    /// <summary>Delete key, Erase tool and the menu's Delete.</summary>
    public void DeleteNotes(IReadOnlyList<Guid> noteIds)
    {
        if (noteIds.Count == 0)
            return;
        Replace(noteIds, Array.Empty<PianoRollNote>());
    }

    /// <summary>Draws a new note (Draw tool), free to overlap any existing note.</summary>
    public PianoRollNote AddNote(double startBeat, double durationBeats, int midiNumber)
    {
        var note = new PianoRollNote(
            Guid.NewGuid(), Math.Clamp(midiNumber, 0, 127), Math.Max(0.0, startBeat), Math.Max(RollEditing.MinNoteBeats, durationBeats),
            Velocity: 100, Origin: PianoRollNoteOrigin.Drawn);
        Replace(Array.Empty<Guid>(), new[] { note });
        return note;
    }

    /// <summary>Split tool: cuts a note in two at a point. Returns both halves, or null if the
    /// point isn't inside the note.</summary>
    public (PianoRollNote Left, PianoRollNote Right)? SplitNote(Guid noteId, double atBeat)
    {
        PianoRollNote? note = RollNotes.FirstOrDefault(n => n.Id == noteId);
        if (note is null || RollEditing.Split(note, atBeat) is not { } halves)
            return null;
        Replace(new[] { noteId }, new[] { halves.Left, halves.Right });
        return halves;
    }

    /// <summary>Replaces each selected note with a chord voicing rooted on it.</summary>
    public IReadOnlyList<PianoRollNote> ApplyChordTransform(IReadOnlyList<Guid> selectedNoteIds, ChordQuality quality)
    {
        var command = new ChordTransformCommand(RollNotes, selectedNoteIds, quality);
        command.Do();
        Push(new TrackScopedCommand(SelectedTrack, command));
        return command.ResultVoices;
    }

    // ---- Clipboard ----

    public static bool HasClipboard => RollClipboard.Notes.Count > 0;

    /// <summary>Paste needs something copied, and isn't available during playback.</summary>
    public bool CanPaste => HasClipboard && !IsPlaying && !IsRecording;

    public bool CanEdit => !IsRecording;

    public void Copy(IReadOnlyList<Guid> noteIds)
    {
        var ids = new HashSet<Guid>(noteIds);
        List<PianoRollNote> copied = RollNotes.Where(n => ids.Contains(n.Id)).ToList();
        if (copied.Count == 0)
            return;
        RollClipboard.Notes = copied;
        OnPropertyChanged(nameof(CanPaste));
    }

    public void Cut(IReadOnlyList<Guid> noteIds)
    {
        Copy(noteIds);
        DeleteNotes(noteIds);
    }

    /// <summary>Pastes at the playhead: the earliest copied note lands there, pitches and spacing
    /// kept. Returns the pasted notes (the caller selects them).</summary>
    public IReadOnlyList<PianoRollNote> Paste()
    {
        if (!CanPaste)
            return Array.Empty<PianoRollNote>();
        IReadOnlyList<PianoRollNote> pasted = RollEditing.PasteAt(RollClipboard.Notes, PositionBeats);
        Replace(Array.Empty<Guid>(), pasted);
        return pasted;
    }

    // ---- History ----

    private void Replace(IReadOnlyList<Guid> removeIds, IReadOnlyList<PianoRollNote> add)
    {
        var command = new ReplaceNotesCommand(RollNotes, removeIds, add);
        command.Do();
        Push(new TrackScopedCommand(SelectedTrack, command));
    }

    private void Undo()
    {
        if (_undoStack.Count == 0)
            return;
        IEditCommand command = _undoStack.Pop();
        command.Undo();
        _redoStack.Push(command);
        SelectAfterHistoryStep(command);
        RaiseEditStateChanged();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0)
            return;
        IEditCommand command = _redoStack.Pop();
        command.Do();
        _undoStack.Push(command);
        SelectAfterHistoryStep(command);
        RaiseEditStateChanged();
    }

    /// <summary>After Undo/Redo, the track that changed is selected (or, if it's gone, the one now
    /// in its place) so you see what happened.</summary>
    private void SelectAfterHistoryStep(IEditCommand command)
    {
        if (command is TrackScopedCommand scoped && Tracks.Contains(scoped.Track))
        {
            SelectedTrack = scoped.Track;
            return;
        }
        if (!Tracks.Contains(SelectedTrack))
        {
            _selectedTrack = null!;
            SelectedTrack = Tracks[Math.Min(_lastSelectedIndexHint, Tracks.Count - 1)];
        }
    }

    private int _lastSelectedIndexHint;

    private void Push(IEditCommand command)
    {
        _undoStack.Push(command);
        _redoStack.Clear();
        RaiseEditStateChanged();
    }

    private void RaiseEditStateChanged()
    {
        OnPropertyChanged(nameof(CanDeleteTrack));
        OnPropertyChanged(nameof(CanUndoEdit));
        OnPropertyChanged(nameof(CanRedoEdit));
        OnPropertyChanged(nameof(CanPaste));
        OnPropertyChanged(nameof(CanEdit));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
