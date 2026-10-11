using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using ToneDetector.Audio.Input;
using ToneDetector.Audio.Synth;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>
/// Owns the app's playback engine (the built-in <see cref="SineSynthesizer"/> or a VST3 plugin,
/// behind <see cref="ISynth"/>) and every attached <see cref="INoteInputSource"/>. Shared (via
/// <see cref="MainViewModel"/>) by the Live Tuner and File Analysis tabs; each tab's
/// <see cref="PianoRollViewModel"/> owns its own transport state (tempo, loop, playhead) and asks
/// this engine to play it, so only one tab plays at a time.
/// Playback runs in beats on a background clock: a <see cref="PlaybackScheduler"/> turns the
/// playhead into note-ons/offs, the tab's tempo turns wall time into beats, and a tempo change
/// mid-play keeps going from the current beat at the new speed. Plays in real time (no silence
/// skipping), so the loop region and bars stay in step with what you hear.
/// The same clock runs while the Live Tuner records (<see cref="StartRecording"/>): no notes, just
/// the moving playhead, the optional 1-bar count-in, and the metronome, a
/// <see cref="ClickSampleProvider"/> mixed into the synth's output so a VST instrument never plays it.
/// Tracks: with the built-in synth each track gets its own voice (a <see cref="TrackMixer"/>), so
/// volume and pan are real; with a VST3 plugin every track shares the one plugin, volume scales
/// velocity and pan does nothing (per-track instruments come later). Muted tracks (and, when any
/// track is soloed, the others) are left out. Keyboard input plays through the
/// <see cref="InputRoll"/>'s selected track.
/// </summary>
public sealed class PlaybackViewModel : INotifyPropertyChanged, IDisposable
{
    private const int ClockIntervalMs = 1;
    private const int PositionUpdateIntervalMs = 30;

    private enum ClockMode { Idle, Playback, Recording }

    private ISynth _synth;
    private ClickSampleProvider _click = null!;

    /// <summary>One built-in voice per track; null when a non-built-in synth (VST3) is active.</summary>
    private TrackMixer? _mixer;

    // Which track each scheduled note belongs to (kept until playback stops, so a note-off for a
    // note that was just deleted still reaches the voice that played it). Guarded by _gate.
    private readonly Dictionary<Guid, Track> _noteTracks = new();

    // The voice each held input key went to, so its note-off follows even if the track changes.
    private readonly Dictionary<int, ISynth> _heldInputVoices = new();
    private readonly IWavePlayer _outputDevice;
    private readonly Dispatcher _dispatcher;
    private readonly bool _runClock;

    // Input sources are wrapped so held keys can light up in the key column.
    private readonly Dictionary<INoteInputSource, (Action<int, int> On, Action<int> Off)> _attachedSources = new();
    private readonly HashSet<int> _heldInputKeys = new();

    // Everything below is shared with the clock thread and guarded by _gate.
    private readonly object _gate = new();
    private readonly PlaybackScheduler _scheduler = new();
    private readonly Stopwatch _clock = new();
    private double _originBeat;
    private double _msPerBeat = MusicalTime.MsPerBeat(MusicalTime.DefaultBpm);
    private CancellationTokenSource? _clockCts;
    private ClockMode _mode = ClockMode.Idle;
    private bool _metronomeOn;
    // Recording: the next whole beat to click, the beat recording starts on (after the count-in),
    // and what to call once the count-in is over.
    private int _nextClickBeat;
    private double _recordStartBeat;
    private Action<double>? _onRecordingStarted;

    private DispatcherTimer? _positionTimer;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised on the UI thread when the keys or notes sounding change.</summary>
    public event EventHandler? SoundingChanged;

    /// <summary>The tab currently playing, or null.</summary>
    public PianoRollViewModel? ActiveRoll { get; private set; }

    /// <summary>The tab whose selected track keyboard input plays through (set on tab change).</summary>
    public PianoRollViewModel? InputRoll { get; set; }

    /// <summary>True when each track has its own built-in voice (real volume and pan).</summary>
    public bool HasTrackVoices => _mixer is not null;

    private bool _isPlaying;
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetField(ref _isPlaying, value);
    }

    /// <summary>True while the recording clock runs (count-in and recording).</summary>
    public bool IsRecordingClock => _mode == ClockMode.Recording;

    /// <summary>How far behind the clock the speakers are (the output buffer), in ms: a take is
    /// moved this much earlier so notes sung with the click land on its beats.</summary>
    public double OutputLatencyMs => _outputDevice is WaveOutEvent waveOut ? waveOut.DesiredLatency : 0.0;

    /// <summary>Keys held on an input (on-screen keys, computer or MIDI keyboard).</summary>
    public IReadOnlyCollection<int> HeldKeys { get; private set; } = Array.Empty<int>();

    /// <summary>Keys of the playback notes sounding right now (in <see cref="ActiveRoll"/>).</summary>
    public IReadOnlyCollection<int> PlaybackKeys { get; private set; } = Array.Empty<int>();

    /// <summary>Roll notes sounding in playback right now (in <see cref="ActiveRoll"/>).</summary>
    public IReadOnlyCollection<Guid> SoundingNoteIds { get; private set; } = Array.Empty<Guid>();

    /// <summary>Master output volume (0.0-1.0), forwarded straight to the output device.</summary>
    public float Volume
    {
        get => _outputDevice.Volume;
        set => _outputDevice.Volume = value;
    }

    /// <summary>The currently active synth - read by the Settings tab's "Show plugin editor" button,
    /// which needs the live <see cref="VstHostSynth"/> instance (not just its path) to open its
    /// editor window. Changes whenever <see cref="SetSynthVoiceCount"/>/
    /// <see cref="SwitchToVst3Plugin"/>/<see cref="SwitchToBuiltInSynth"/> swaps it out.</summary>
    public ISynth CurrentSynth => _synth;

    public PlaybackViewModel(Func<ISynth>? synthFactory = null, Func<IWavePlayer>? outputDeviceFactory = null)
        : this(synthFactory, outputDeviceFactory, runClock: true)
    {
    }

    /// <summary><paramref name="runClock"/> false leaves the clock to the test (see <see cref="AdvanceForTest"/>).</summary>
    internal PlaybackViewModel(Func<ISynth>? synthFactory, Func<IWavePlayer>? outputDeviceFactory, bool runClock)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        _runClock = runClock;
        _synth = synthFactory?.Invoke() ?? new SineSynthesizer();
        _outputDevice = outputDeviceFactory?.Invoke() ?? new WaveOutEvent();
        StartOutput();
    }

    /// <summary>Routes the synth (the built-in one as a voice per track), with the metronome mixed
    /// in, to the output device.</summary>
    private void StartOutput()
    {
        _mixer = _synth is SineSynthesizer sine ? new TrackMixer(sine.MaxVoices) : null;
        _click = new ClickSampleProvider(_mixer ?? _synth.Output);
        _outputDevice.Init(_click.ToWaveProvider());
        _outputDevice.Play();
    }

    // ---- Input sources ----

    /// <summary>Subscribes to a note-input source (USB MIDI keyboard, computer keyboard, or the
    /// on-screen keys) and starts it - every event it raises plays straight through to the active
    /// synth. Any number of sources can be attached and live simultaneously.</summary>
    public void AttachInputSource(INoteInputSource source)
    {
        if (_attachedSources.ContainsKey(source))
            return;

        void On(int note, int velocity)
        {
            InputNoteOn(note, velocity);
            OnInputKey(note, held: true);
        }

        void Off(int note)
        {
            InputNoteOff(note);
            OnInputKey(note, held: false);
        }

        source.NoteOn += On;
        source.NoteOff += Off;
        _attachedSources[source] = (On, Off);
        source.Start();
    }

    public void DetachInputSource(INoteInputSource source)
    {
        if (!_attachedSources.Remove(source, out var handlers))
            return;
        source.NoteOn -= handlers.On;
        source.NoteOff -= handlers.Off;
        source.Stop();
    }

    /// <summary>Input plays through the input tab's selected track (its own voice, or the shared
    /// synth at the track's volume). Input events can arrive on a MIDI thread.</summary>
    private void InputNoteOn(int note, int velocity)
    {
        Track? track = InputRoll?.SelectedTrack;
        ISynth voice;
        lock (_gate)
        {
            voice = track is null ? (_mixer?.VoiceFor(Guid.Empty) ?? _synth) : VoiceForLocked(track);
            if (_heldInputVoices.TryGetValue(note, out ISynth? previous) && !ReferenceEquals(previous, voice))
                previous.NoteOff(note);
            _heldInputVoices[note] = voice;
        }
        int scaled = track is null ? velocity : ScaledVelocity(velocity, track);
        if (scaled > 0)
            voice.NoteOn(note, scaled);
    }

    private void InputNoteOff(int note)
    {
        ISynth voice;
        lock (_gate)
        {
            if (!_heldInputVoices.Remove(note, out ISynth? held))
                held = _mixer is null ? _synth : null;
            if (held is null)
                return;
            voice = held;
        }
        voice.NoteOff(note);
    }

    /// <summary>The synth a track's notes go to: its own voice (at its level and pan), or the shared synth.</summary>
    private ISynth VoiceForLocked(Track track) =>
        _mixer?.VoiceFor(track.Id, (float)track.Gain, track.Pan / 100f) ?? _synth;

    /// <summary>With its own voice a track's level is the voice's volume; with the shared synth it
    /// scales velocity (0 when silent).</summary>
    private int ScaledVelocity(int velocity, Track track)
    {
        if (_mixer is not null)
            return velocity;
        double gain = track.Gain;
        if (gain <= 0)
            return 0;
        return Math.Clamp((int)Math.Round(velocity * gain), 1, 127);
    }

    private void OnInputKey(int note, bool held)
    {
        if (_dispatcher.CheckAccess())
            ApplyInputKey(note, held);
        else
            _dispatcher.BeginInvoke(() => ApplyInputKey(note, held));
    }

    private void ApplyInputKey(int note, bool held)
    {
        bool changed = held ? _heldInputKeys.Add(note) : _heldInputKeys.Remove(note);
        if (changed)
            PublishSounding();
    }

    // ---- Synth ----

    /// <summary>Rebuilds the active synth as a fresh <see cref="SineSynthesizer"/> with the given
    /// voice pool size. Voice count isn't part of <see cref="ISynth"/> (a VST3 host doesn't have a
    /// "voice count" knob in the same sense), so this always targets <see cref="SineSynthesizer"/>.</summary>
    public void SetSynthVoiceCount(int maxVoices) => SwapSynth(() => new SineSynthesizer(maxVoices));

    /// <summary>Switches the active synth to a loaded VST3 plugin. Throws if the plugin fails to
    /// load/initialize (see <see cref="VstHostSynth"/>'s constructor) - the caller (MainWindow's
    /// Settings handler) catches this and tells the user; the previous synth is untouched until the
    /// new one has been constructed successfully.</summary>
    public void SwitchToVst3Plugin(string pluginPath) => SwapSynth(() => new VstHostSynth(pluginPath));

    /// <summary>Switches back to the built-in sine synth, e.g. after a VST3 plugin was active.</summary>
    public void SwitchToBuiltInSynth() => SwapSynth(() => new SineSynthesizer());

    /// <summary>Construct the replacement first (so a failure leaves the current synth running),
    /// then stop playback, release the old synth and restart the output device on the new one.
    /// Input sources call through <see cref="_synth"/> at event time, so they follow automatically.</summary>
    private void SwapSynth(Func<ISynth> newSynthFactory)
    {
        ISynth newSynth = newSynthFactory();

        StopPlayback();

        _synth.AllNotesOff();
        if (_synth is IDisposable disposableOldSynth)
            disposableOldSynth.Dispose();

        _mixer?.AllNotesOff();
        lock (_gate)
            _heldInputVoices.Clear();
        _synth = newSynth;

        _outputDevice.Stop();
        StartOutput();

        OnPropertyChanged(nameof(CurrentSynth));
    }

    // ---- Transport ----

    /// <summary>Plays a tab's notes from its playhead (from 0 when the playhead is past the last
    /// note and outside the loop). Stops whatever else was playing.</summary>
    public void Play(PianoRollViewModel roll)
    {
        // Read the playhead first: stopping the current playback writes its own position back.
        double start = roll.PositionBeats;
        StopPlayback();

        double end = RollEditing.EndBeat(roll.AllNotes);
        bool insideLoop = roll.IsLoopEnabled && start < roll.LoopEndBeat;
        if (start >= end && !insideLoop)
            start = roll.IsLoopEnabled ? roll.LoopStartBeat : 0.0;

        ActiveRoll = roll;
        roll.PlaybackNotesChanged += ActiveRoll_NotesChanged;
        roll.MixChanged += ActiveRoll_MixChanged;
        roll.PropertyChanged += ActiveRoll_PropertyChanged;
        PruneVoices(roll);

        IReadOnlyList<NoteEvent> events;
        IReadOnlyList<int> beats;
        lock (_gate)
        {
            _mode = ClockMode.Playback;
            _metronomeOn = roll.IsMetronomeOn;
            _msPerBeat = MusicalTime.MsPerBeat(roll.Bpm);
            _scheduler.Loop = LoopOf(roll);
            SetScheduledNotesLocked(roll);
            events = _scheduler.Start(start);
            beats = _scheduler.DrainCrossedBeats();
            _originBeat = start;
            RestartClockLocked();
        }
        Send(events);
        ClickFor(beats);

        roll.PositionBeats = start;
        IsPlaying = true;
        roll.IsPlaying = true;
        OnPropertyChanged(nameof(ActiveRoll));
        PublishSounding();

        StartPositionTimer();
        if (_runClock)
            StartClock();
    }

    /// <summary>Starts the recording clock for a tab from its playhead: the playhead moves, the
    /// metronome ticks if it's on, and after <paramref name="countInBeats"/> (0 = straight away)
    /// <paramref name="onRecordingStarted"/> is called on the UI thread with the beat recording
    /// starts on. Stops whatever was playing. <see cref="StopPlayback"/> ends it.</summary>
    public void StartRecording(PianoRollViewModel roll, int countInBeats, Action<double> onRecordingStarted)
    {
        double start = roll.PositionBeats;
        StopPlayback();

        ActiveRoll = roll;
        roll.PropertyChanged += ActiveRoll_PropertyChanged;
        lock (_gate)
        {
            _mode = ClockMode.Recording;
            _metronomeOn = roll.IsMetronomeOn;
            _msPerBeat = MusicalTime.MsPerBeat(roll.Bpm);
            _scheduler.Loop = null;
            _scheduler.SetNotes(Array.Empty<PianoRollNote>());
            _scheduler.Start(0);
            _scheduler.DrainCrossedBeats();
            _recordStartBeat = start;
            _originBeat = start - Math.Max(0, countInBeats);
            _nextClickBeat = (int)Math.Ceiling(_originBeat - 1e-6);
            _onRecordingStarted = countInBeats > 0 ? onRecordingStarted : null;
            RestartClockLocked();
        }
        OnPropertyChanged(nameof(ActiveRoll));
        OnPropertyChanged(nameof(IsRecordingClock));

        if (countInBeats <= 0)
            onRecordingStarted(start);
        else
            ClickDueBeats(); // the count-in's first tick

        StartPositionTimer();
        if (_runClock)
            StartClock();
    }

    /// <summary>Halts playback and leaves the playhead where it is (Pause, or before Stop moves it).</summary>
    public void StopPlayback()
    {
        _clockCts?.Cancel();
        _clockCts = null;
        _positionTimer?.Stop();

        IReadOnlyList<NoteEvent> released;
        double position;
        bool wasRecording;
        lock (_gate)
        {
            released = _scheduler.ReleaseAll();
            position = CurrentBeatLocked();
            wasRecording = _mode == ClockMode.Recording;
            _mode = ClockMode.Idle;
            _onRecordingStarted = null;
            _clock.Reset();
        }
        Send(released);
        lock (_gate)
            _noteTracks.Clear();
        if (_mixer is null)
            _synth.AllNotesOff();
        else
            ReleasePlaybackVoices();

        PianoRollViewModel? roll = ActiveRoll;
        if (roll is not null)
        {
            roll.PlaybackNotesChanged -= ActiveRoll_NotesChanged;
            roll.MixChanged -= ActiveRoll_MixChanged;
            roll.PropertyChanged -= ActiveRoll_PropertyChanged;
            if (IsPlaying || wasRecording)
                roll.PositionBeats = position;
            roll.IsPlaying = false;
            ActiveRoll = null;
            OnPropertyChanged(nameof(ActiveRoll));
        }

        IsPlaying = false;
        if (wasRecording)
            OnPropertyChanged(nameof(IsRecordingClock));
        PublishSounding();
    }

    private static (double Start, double End)? LoopOf(PianoRollViewModel roll) =>
        roll.IsLoopEnabled && roll.LoopEndBeat > roll.LoopStartBeat ? (roll.LoopStartBeat, roll.LoopEndBeat) : null;

    /// <summary>Notes, tracks or mute/solo changed mid-play: reschedule (notes that went away stop).</summary>
    private void ActiveRoll_NotesChanged(object? sender, EventArgs e)
    {
        if (ActiveRoll is not { } roll || _mode != ClockMode.Playback)
            return;
        IReadOnlyList<NoteEvent> events;
        lock (_gate)
            events = SetScheduledNotesLocked(roll);
        Send(events);
        PublishSounding();
    }

    private void ActiveRoll_MixChanged(object? sender, EventArgs e)
    {
        if (ActiveRoll is { } roll)
            ApplyMix(roll);
    }

    /// <summary>Volume and pan to each track's voice (built-in synth only).</summary>
    public void ApplyMix(PianoRollViewModel roll)
    {
        if (_mixer is null)
            return;
        foreach (Track track in roll.Tracks)
            _mixer.SetMix(track.Id, (float)track.Gain, track.Pan / 100f);
    }

    private IReadOnlyList<NoteEvent> SetScheduledNotesLocked(PianoRollViewModel roll)
    {
        IReadOnlyList<(PianoRollNote Note, Track Track)> audible = roll.AudibleNotes();
        foreach ((PianoRollNote note, Track track) in audible)
            _noteTracks[note.Id] = track;
        return _scheduler.SetNotes(audible.Select(a => a.Note).ToList());
    }

    /// <summary>Drops the voices of tracks that no longer exist in the playing tab or the input
    /// tab (a new voice is made if a track comes back through Undo).</summary>
    private void PruneVoices(PianoRollViewModel roll)
    {
        if (_mixer is null)
            return;
        var keep = new HashSet<Guid>(roll.Tracks.Select(t => t.Id)) { Guid.Empty };
        if (InputRoll is { } input)
            keep.UnionWith(input.Tracks.Select(t => t.Id));
        foreach (Guid id in _mixer.TrackIds)
        {
            if (!keep.Contains(id))
                _mixer.Remove(id);
        }
        ApplyMix(roll);
    }

    /// <summary>Stops playback notes on every voice except keys still held on an input.</summary>
    private void ReleasePlaybackVoices()
    {
        if (_mixer is null)
            return;
        HashSet<ISynth> held;
        lock (_gate)
            held = new HashSet<ISynth>(_heldInputVoices.Values);
        foreach (Guid id in _mixer.TrackIds)
        {
            ISynth voice = _mixer.VoiceFor(id);
            if (!held.Contains(voice))
                voice.AllNotesOff();
        }
    }

    private void ActiveRoll_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ActiveRoll is not { } roll)
            return;

        switch (e.PropertyName)
        {
            case nameof(PianoRollViewModel.Bpm):
                lock (_gate)
                {
                    // Keep going from the current beat at the new speed.
                    _originBeat = CurrentBeatLocked();
                    _msPerBeat = MusicalTime.MsPerBeat(roll.Bpm);
                    RestartClockLocked();
                }
                break;
            case nameof(PianoRollViewModel.IsLoopEnabled):
            case nameof(PianoRollViewModel.LoopStartBeat):
            case nameof(PianoRollViewModel.LoopEndBeat):
                if (_mode != ClockMode.Playback)
                    break;
                lock (_gate)
                    _scheduler.Loop = LoopOf(roll);
                break;
            case nameof(PianoRollViewModel.IsMetronomeOn):
                lock (_gate)
                    _metronomeOn = roll.IsMetronomeOn;
                break;
        }
    }

    /// <summary>Tests drive the clock themselves, so it stays stopped for them.</summary>
    private void RestartClockLocked()
    {
        if (_runClock)
            _clock.Restart();
        else
            _clock.Reset();
    }

    /// <summary>Beat the clock says we're at: while recording, the clock itself (it can be before
    /// the start during the count-in); while playing, the scheduler position plus elapsed time.</summary>
    private double CurrentBeatLocked() =>
        _mode == ClockMode.Recording ? _originBeat + _clock.Elapsed.TotalMilliseconds / _msPerBeat
        : _clock.IsRunning ? _originBeat + _clock.Elapsed.TotalMilliseconds / _msPerBeat
        : _scheduler.PositionBeats;

    // ---- Clock ----

    private void StartClock()
    {
        var cts = new CancellationTokenSource();
        _clockCts = cts;
        var thread = new Thread(() => RunClock(cts.Token))
        {
            IsBackground = true,
            Name = "Music Station playback clock",
            Priority = ThreadPriority.AboveNormal,
        };
        thread.Start();
    }

    private void RunClock(CancellationToken token)
    {
        bool raisedTimerResolution = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                if (Tick(token))
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        if (_clockCts?.Token == token)
                            FinishNaturally();
                    });
                    return;
                }
                Thread.Sleep(ClockIntervalMs);
            }
        }
        finally
        {
            if (raisedTimerResolution)
                TimeEndPeriod(1);
        }
    }

    /// <summary>One clock step: advance to the current beat and send what starts/stops. True once
    /// playback has run past the last note (not while looping).</summary>
    private bool Tick(CancellationToken token)
    {
        if (_mode == ClockMode.Recording)
        {
            ClickDueBeats();
            return false;
        }

        IReadOnlyList<NoteEvent> events;
        IReadOnlyList<int> beats;
        bool finished;
        lock (_gate)
        {
            if (token.IsCancellationRequested || _mode != ClockMode.Playback)
                return false;
            double target = _originBeat + _clock.Elapsed.TotalMilliseconds / _msPerBeat;
            events = _scheduler.AdvanceTo(target);
            beats = _scheduler.DrainCrossedBeats();
            if (_scheduler.PositionBeats < target - 1e-9)
            {
                // Wrapped at the loop end: restart the clock from the loop start.
                _originBeat = _scheduler.PositionBeats;
                RestartClockLocked();
            }
            finished = _scheduler.IsFinished;
        }
        Send(events);
        ClickFor(beats);
        if (events.Count > 0)
            _dispatcher.BeginInvoke(PublishSounding);
        return finished;
    }

    /// <summary>Recording: ticks every whole beat the clock has reached, and ends the count-in.</summary>
    private void ClickDueBeats()
    {
        var beats = new List<int>();
        Action<double>? started = null;
        double startBeat = 0;
        lock (_gate)
        {
            if (_mode != ClockMode.Recording)
                return;
            double beat = CurrentBeatLocked();
            while (_nextClickBeat <= beat + 1e-6)
                beats.Add(_nextClickBeat++);
            if (_onRecordingStarted is not null && beat >= _recordStartBeat - 1e-6)
            {
                started = _onRecordingStarted;
                startBeat = beat;
                _onRecordingStarted = null;
            }
        }
        ClickFor(beats);
        if (started is null)
            return;
        if (_dispatcher.CheckAccess())
            started(startBeat);
        else
            _dispatcher.BeginInvoke(() =>
            {
                if (_mode == ClockMode.Recording)
                    started(startBeat);
            });
    }

    /// <summary>One tick for the latest beat reached (several at once only after a stall), higher on beat 1 of a bar.</summary>
    private void ClickFor(IReadOnlyList<int> beats)
    {
        if (beats.Count == 0 || !_metronomeOn)
            return;
        int beat = beats[^1];
        bool accent = ((beat % MusicalTime.BeatsPerBar) + MusicalTime.BeatsPerBar) % MusicalTime.BeatsPerBar == 0;
        _click.Trigger(accent);
        ClickCountForTest++;
        LastClickWasAccentForTest = accent;
    }

    private void FinishNaturally()
    {
        PianoRollViewModel? roll = ActiveRoll;
        double end;
        lock (_gate)
            end = _scheduler.PositionBeats;
        StopPlayback();
        if (roll is not null)
            roll.PositionBeats = end;
    }

    private void Send(IReadOnlyList<NoteEvent> events)
    {
        foreach (NoteEvent evt in events)
        {
            Track? track;
            ISynth voice;
            lock (_gate)
            {
                _noteTracks.TryGetValue(evt.NoteId, out track);
                voice = track is null ? _synth : VoiceForLocked(track);
            }
            if (evt.Kind == NoteEventKind.On)
            {
                int velocity = track is null ? evt.Velocity : ScaledVelocity(evt.Velocity, track);
                if (velocity > 0)
                    voice.NoteOn(evt.MidiNumber, velocity);
            }
            else
            {
                voice.NoteOff(evt.MidiNumber);
            }
        }
    }

    private void StartPositionTimer()
    {
        _positionTimer ??= CreatePositionTimer();
        _positionTimer.Start();
    }

    private DispatcherTimer CreatePositionTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(PositionUpdateIntervalMs) };
        timer.Tick += (_, _) => UpdatePosition();
        return timer;
    }

    private void UpdatePosition()
    {
        if (ActiveRoll is not { } roll)
            return;
        double beat;
        lock (_gate)
            beat = _mode == ClockMode.Recording ? Math.Max(_recordStartBeat, CurrentBeatLocked()) : CurrentBeatLocked();
        roll.PositionBeats = beat;
    }

    private void PublishSounding()
    {
        List<Guid> noteIds;
        HashSet<int> playbackKeys;
        lock (_gate)
        {
            noteIds = IsPlaying ? SoundingIdsLocked() : new List<Guid>();
            playbackKeys = IsPlaying ? new HashSet<int>(_scheduler.SoundingMidiNumbers) : new HashSet<int>();
        }

        bool same = playbackKeys.SetEquals(PlaybackKeys)
            && _heldInputKeys.SetEquals(HeldKeys)
            && noteIds.Count == SoundingNoteIds.Count && noteIds.All(SoundingNoteIds.Contains);
        if (same)
            return;

        PlaybackKeys = playbackKeys.ToArray();
        HeldKeys = _heldInputKeys.ToArray();
        SoundingNoteIds = noteIds;
        SoundingChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<Guid> SoundingIdsLocked() =>
        ActiveRoll is { } roll ? roll.AllNotes.Where(n => _scheduler.IsSounding(n.Id)).Select(n => n.Id).ToList() : new List<Guid>();

    public void Dispose()
    {
        StopPlayback();
        foreach (INoteInputSource source in _attachedSources.Keys.ToList())
            DetachInputSource(source);
        if (_synth is IDisposable disposableSynth)
            disposableSynth.Dispose(); // e.g. VstHostSynth unloading its native plugin
        _outputDevice.Stop();
        _outputDevice.Dispose();
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint milliseconds);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint milliseconds);

    internal ISynth SynthForTest => _synth;
    internal int ClickCountForTest { get; private set; }
    internal bool LastClickWasAccentForTest { get; private set; }

    /// <summary>Test-only: moves the clock forward by <paramref name="beats"/> and runs one step,
    /// as the clock thread would (constructed with runClock false).</summary>
    internal void AdvanceForTest(double beats)
    {
        lock (_gate)
            _originBeat += beats;
        bool finished = Tick(CancellationToken.None);
        UpdatePosition();
        PublishSounding();
        if (finished)
            FinishNaturally();
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
