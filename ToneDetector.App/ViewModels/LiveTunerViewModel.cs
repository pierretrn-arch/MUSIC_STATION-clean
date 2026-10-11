using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using ToneDetector.Audio;
using ToneDetector.Core.Analysis;
using ToneDetector.Core.Diagnostics;
using ToneDetector.Core.Framing;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Pitch;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>Current detected note for hero display, split into name and octave (mirrors what
/// the plan's DisplayNote formatting expects to read from).</summary>
public readonly record struct PitchDisplay(string Note, int Octave);

/// <summary>
/// Drives the live tuner view. Audio capture and pitch detection (the "pipeline") run
/// continuously whenever a device is selected, independent of whether the user is actively
/// recording: <see cref="IsPipelineRunning"/> tracks the former, <see cref="IsRecording"/> the
/// latter. The hero display and status strip always reflect the pipeline; only committed
/// transcript events are gated by <see cref="IsRecording"/>, via <see cref="LiveTranscriptBuilder"/>.
/// </summary>
public sealed class LiveTunerViewModel : INotifyPropertyChanged, IDisposable
{
    private static readonly Brush InTuneBrush = Brushes.LimeGreen;
    private static readonly Brush CloseBrush = Brushes.Orange;
    private static readonly Brush OffPitchBrush = Brushes.OrangeRed;
    private static readonly Brush UnvoicedBrush = Brushes.Gray;

    private const int WindowSize = 4096;
    private const int HopSize = 1024;

    private readonly IAudioDeviceManager _deviceManager;
    private readonly PitchAnalysisPipeline _pipeline = new();
    private readonly Dispatcher _dispatcher;
    private readonly LiveTranscriptBuilder _transcriptBuilder;
    private readonly List<TranscriptEvent> _committedEvents = new();
    private readonly KeyDetector _keyDetector = new();
    private readonly Func<IAudioCapture> _captureFactory;

    private readonly PipelineSettingsViewModel _settings;
    private IAudioCapture? _capture;
    private SlidingWindowFrameAccumulator? _accumulator;
    private double _frameDurationMs;
    private double? _lastEventEndMs;

    // DETECTION-DIAGNOSTICS-PRESETS.md Change 10. LogFrame is called from the real-time audio
    // thread (see OnFrameReady) so the logger only enqueues there; this timer does the actual file
    // write off that thread, at a fixed cadence independent of frame rate. Injectable so tests can
    // point it at a temp file instead of the real %AppData% log.
    private readonly VoicingDiagnosticsLogger _diagnosticsLogger;
    private readonly System.Threading.Timer _diagnosticsFlushTimer;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MMDevice> CaptureDevices { get; } = new();
    public ObservableCollection<TranscriptEntryViewModel> Transcript { get; } = new();

    /// <summary>Filtered view of <see cref="Transcript"/> for the Grid tab - Silence rows stay in
    /// the real underlying data (gap-since-prev math, export, and the segmentation logic itself all
    /// still need them), only the Grid's display is filtered. The Piano Roll never draws a bar for
    /// Silence entries in the first place, so it needs no equivalent filtering.</summary>
    public ICollectionView TranscriptView { get; }

    private bool _hideSilenceRows = true;
    /// <summary>Defaults to hidden (decluttering was the point of this toggle) but easy to flip back
    /// on - this whole feature started from a segmentation bug (DETECTION-DIAGNOSTICS-PRESETS.md)
    /// where seeing Silence rows (or their absence) was the actual diagnostic signal.</summary>
    public bool HideSilenceRows
    {
        get => _hideSilenceRows;
        set
        {
            if (SetField(ref _hideSilenceRows, value))
                TranscriptView.Refresh();
        }
    }

    /// <summary>True when Windows exposes no active capture endpoints at all (e.g. a driver
    /// fault on the built-in mic) — used to show a helpful message instead of a silently empty
    /// device dropdown.</summary>
    public bool HasNoDevices => CaptureDevices.Count == 0;

    private MMDevice? _selectedDevice;
    public MMDevice? SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (Equals(_selectedDevice, value))
                return;
            _selectedDevice = value;
            OnPropertyChanged(nameof(SelectedDevice));
            RaiseDeviceChanged();
            RestartPipelineForDeviceChange();
        }
    }

    private bool _isListeningStoppedByUser;
    /// <summary>True after Stop listening, until Start listening. Session-only: the app always
    /// starts listening on launch. While set, neither Windows device events nor picking a new input
    /// restart the pipeline; the selected device is still tracked and used on the next Start.</summary>
    public bool IsListeningStoppedByUser
    {
        get => _isListeningStoppedByUser;
        private set => SetField(ref _isListeningStoppedByUser, value);
    }

    private PitchDisplay? _currentPitch;
    public PitchDisplay? CurrentPitch
    {
        get => _currentPitch;
        internal set // internal (rather than private) so unit tests can drive hero-display state without real audio capture
        {
            if (SetField(ref _currentPitch, value))
            {
                OnPropertyChanged(nameof(DisplayNote));
                OnPropertyChanged(nameof(HeroOpacity));
                RaisePitchReadoutsChanged();
            }
        }
    }

    /// <summary>Hero note display: bound to the pipeline (not recording state) — "Not listening"
    /// while the pipeline is off (the same state as the Home status pill), "Listening…" while it
    /// runs without a voiced pitch, otherwise the note name with "#" for sharps (e.g. "C#3"; IBM Plex has no ♯ glyph).</summary>
    public string DisplayNote =>
        !IsPipelineRunning ? "Not listening"
        : CurrentPitch is null ? "Listening…"
        : FormatNote(CurrentPitch.Value.Note, CurrentPitch.Value.Octave);

    /// <summary>Dims the hero note while idle (no size change, so the layout doesn't jump the
    /// moment a note is detected) and shows it at full strength once a pitch is being read.</summary>
    public double HeroOpacity => !IsPipelineRunning || CurrentPitch is null ? 0.35 : 1.0;

    // ================= Home pitch panel readouts =================

    /// <summary>Length of the Home pitch-history trace.</summary>
    public const double PitchHistoryWindowSeconds = 5.0;

    /// <summary>Offsets within this many cents (after rounding to whole cents, as displayed) read
    /// as in tune. Matches the width of the in-tune band on the Home tuning meter.</summary>
    public const double InTuneToleranceCents = 5.0;

    private const string NoValue = "—";
    private const char MinusSign = '−'; // U+2212, per the design README
    // Readouts format with InvariantCulture: a decimal point whatever the Windows region, as in the mockups.

    /// <summary>Cents offsets for the Home trace, oldest first; trimmed to <see cref="PitchHistoryWindowSeconds"/>.</summary>
    public PitchHistoryBuffer PitchHistory { get; } = new(PitchHistoryWindowSeconds * 1000.0);

    // Pipeline time of the next frame, advanced by one hop per processed frame.
    private double _pitchClockMs;

    /// <summary>True while listening and a voiced pitch is being read.</summary>
    public bool HasPitch => IsPipelineRunning && CurrentPitch is not null;

    /// <summary>Hero note letter ("C#"), or "—" with no pitch.</summary>
    public string NoteLetter => HasPitch ? ToGlyphs(CurrentPitch!.Value.Note) : NoValue;

    /// <summary>Hero octave digit, or empty with no pitch.</summary>
    public string NoteOctave => HasPitch ? CurrentPitch!.Value.Octave.ToString() : "";

    /// <summary>Equal-tempered frequency of the detected note (A4 = 440 Hz), or null with no pitch.</summary>
    public double? TargetFrequencyHz =>
        HasPitch && NoteQuantizer.TryComputeMidiNumber(CurrentPitch!.Value.Note, CurrentPitch.Value.Octave, out int midi)
            ? NoteQuantizer.AbsoluteCentsToFrequency(midi * 100.0)
            : null;

    /// <summary>MIDI number of the detected note, for the piano roll's detected key; null with no pitch.</summary>
    public int? DetectedMidiNote =>
        HasPitch && NoteQuantizer.TryComputeMidiNumber(CurrentPitch!.Value.Note, CurrentPitch.Value.Octave, out int midi)
            ? midi
            : null;

    public string DetectedFrequencyText => HasPitch ? DetectedFrequencyHz.ToString("0.00", CultureInfo.InvariantCulture) : NoValue;

    public string TargetFrequencyText => TargetFrequencyHz is double hz ? hz.ToString("0.00", CultureInfo.InvariantCulture) : NoValue;

    /// <summary>Cents offset for the tuning meter needle, or null (needle hidden) with no pitch.</summary>
    public double? DisplayCents => HasPitch ? CentsDeviation : null;

    public TuningState TuningState => HasPitch ? ClassifyTuning(CentsDeviation) : TuningState.None;

    /// <summary>Signed whole cents with a real minus sign ("+2", "−3", "0"), or "—" with no pitch.</summary>
    public string OffsetText => HasPitch ? FormatSignedCents(CentsDeviation) : NoValue;

    public string OffsetLabel => TuningState switch
    {
        TuningState.InTune => "cents · in tune",
        TuningState.Flat => "cents · flat",
        TuningState.Sharp => "cents · sharp",
        _ => "cents",
    };

    /// <summary>Tuner panel, next to the Offset label: "In tune", "Flat", "Sharp", or empty with no pitch.</summary>
    public string OffsetStatusText => TuningState switch
    {
        TuningState.InTune => "In tune",
        TuningState.Flat => "Flat",
        TuningState.Sharp => "Sharp",
        _ => "",
    };

    /// <summary>Collapsed tuner strip: "E4 · 329.06 Hz", or "No pitch · — Hz" / "Not listening".</summary>
    public string CompactPitchText =>
        HasPitch ? $"{NoteLetter}{NoteOctave} · {DetectedFrequencyText} Hz"
        : IsPipelineRunning ? $"No pitch · {NoValue} Hz"
        : "";

    private int? _sampleRateHz;
    /// <summary>Capture sample rate while listening, else null.</summary>
    public int? SampleRateHz
    {
        get => _sampleRateHz;
        private set
        {
            if (SetField(ref _sampleRateHz, value))
                OnPropertyChanged(nameof(SampleRateText));
        }
    }

    public bool HasDevice => SelectedDevice is not null;

    /// <summary>Header input device: the full name (the header truncates it and shows this in a
    /// tooltip), or "No input device".</summary>
    public string DeviceName => SelectedDevice?.FriendlyName ?? "No input device";

    /// <summary>Header sample rate: "48 kHz" while listening, "— kHz" while not.</summary>
    public string SampleRateText => SampleRateHz is int rate
        ? $"{(rate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} kHz"
        : $"{NoValue} kHz";

    /// <summary>Header pill note ("C#4"), or empty with no pitch.</summary>
    public string PitchNoteText => HasPitch ? NoteLetter + NoteOctave : "";

    /// <summary>Header pill detail after the note: "in tune", or the signed offset
    /// ("−12 cents" / "+12 cents") when out of tune; empty with no pitch.</summary>
    public string PitchStatusDetail => TuningState switch
    {
        TuningState.None => "",
        TuningState.InTune => "in tune",
        _ => $"{OffsetText} cents",
    };

    /// <summary>Home big-note stand-in when there's no pitch: "No pitch" while listening,
    /// "Not listening" while the pipeline is off.</summary>
    public string NoPitchText => IsPipelineRunning ? "No pitch" : "Not listening";

    private void RaiseDeviceChanged()
    {
        OnPropertyChanged(nameof(HasDevice));
        OnPropertyChanged(nameof(DeviceName));
    }

    internal static TuningState ClassifyTuning(double cents)
    {
        double rounded = Math.Round(cents, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) <= InTuneToleranceCents)
            return TuningState.InTune;
        return rounded < 0 ? TuningState.Flat : TuningState.Sharp;
    }

    internal static string FormatSignedCents(double cents)
    {
        double rounded = Math.Round(cents, MidpointRounding.AwayFromZero);
        if (rounded == 0)
            return "0";
        string magnitude = Math.Abs(rounded).ToString("0", CultureInfo.InvariantCulture);
        return rounded > 0 ? $"+{magnitude}" : $"{MinusSign}{magnitude}";
    }

    private void RaisePitchReadoutsChanged()
    {
        OnPropertyChanged(nameof(HasPitch));
        OnPropertyChanged(nameof(NoteLetter));
        OnPropertyChanged(nameof(NoteOctave));
        OnPropertyChanged(nameof(TargetFrequencyHz));
        OnPropertyChanged(nameof(TargetFrequencyText));
        OnPropertyChanged(nameof(DetectedFrequencyText));
        OnPropertyChanged(nameof(DisplayCents));
        OnPropertyChanged(nameof(TuningState));
        OnPropertyChanged(nameof(OffsetText));
        OnPropertyChanged(nameof(OffsetLabel));
        OnPropertyChanged(nameof(OffsetStatusText));
        OnPropertyChanged(nameof(CompactPitchText));
        OnPropertyChanged(nameof(PitchNoteText));
        OnPropertyChanged(nameof(PitchStatusDetail));
        OnPropertyChanged(nameof(NoPitchText));
        OnPropertyChanged(nameof(DetectedMidiNote));
    }

    /// <summary>Note names already use "#" for sharps (Design/README.md: Plex has no ♯).</summary>
    private static string ToGlyphs(string note) => note;

    private static string FormatNote(string note, int octave)
    {
        return $"{ToGlyphs(note)}{octave}";
    }

    private double _centsDeviation;
    public double CentsDeviation
    {
        get => _centsDeviation;
        private set => SetField(ref _centsDeviation, value);
    }

    private double _detectedFrequencyHz;
    public double DetectedFrequencyHz
    {
        get => _detectedFrequencyHz;
        private set => SetField(ref _detectedFrequencyHz, value);
    }

    private bool _isVoiced;
    public bool IsVoiced
    {
        get => _isVoiced;
        private set => SetField(ref _isVoiced, value);
    }

    private double _needleOffsetPixels;
    public double NeedleOffsetPixels
    {
        get => _needleOffsetPixels;
        private set => SetField(ref _needleOffsetPixels, value);
    }

    private Brush _meterBrush = UnvoicedBrush;
    public Brush MeterBrush
    {
        get => _meterBrush;
        private set => SetField(ref _meterBrush, value);
    }

    private double? _currentVibratoRateHz;
    public double? CurrentVibratoRateHz
    {
        get => _currentVibratoRateHz;
        private set => SetField(ref _currentVibratoRateHz, value);
    }

    private double? _currentVibratoExtentCents;
    public double? CurrentVibratoExtentCents
    {
        get => _currentVibratoExtentCents;
        private set => SetField(ref _currentVibratoExtentCents, value);
    }

    // Populated by key/vibrato analysis; each stays empty until enough notes have
    // accumulated for a confident estimate. StatusStripText joins whichever of these are
    // currently non-empty into a single display line.
    private string _keyDisplayText = "";
    private string _vibratoDisplayText = "";

    public string StatusStripText
    {
        get
        {
            var parts = new List<string>(3);
            if (_keyDisplayText.Length > 0) parts.Add($"Key: {_keyDisplayText}");
            if (_vibratoDisplayText.Length > 0) parts.Add($"Vibrato: {_vibratoDisplayText}");
            return string.Join("   ·   ", parts);
        }
    }

    private void SetKeyDisplayText(string value)
    {
        if (_keyDisplayText == value) return;
        _keyDisplayText = value;
        OnPropertyChanged(nameof(StatusStripText));
    }

    private void SetVibratoDisplayText(string value)
    {
        if (_vibratoDisplayText == value) return;
        _vibratoDisplayText = value;
        OnPropertyChanged(nameof(StatusStripText));
    }

    private bool _isPipelineRunning;
    /// <summary>True whenever audio capture + pitch detection is active (independent of recording).</summary>
    public bool IsPipelineRunning
    {
        get => _isPipelineRunning;
        internal set // internal so unit tests can drive hero-display state without real audio capture
        {
            if (SetField(ref _isPipelineRunning, value))
            {
                OnPropertyChanged(nameof(DisplayNote));
                OnPropertyChanged(nameof(HeroOpacity));
                RaisePitchReadoutsChanged();
            }
        }
    }

    private bool _isRecording;
    /// <summary>True only between Start and Stop: gates whether committed transcript events are kept.</summary>
    public bool IsRecording
    {
        get => _isRecording;
        internal set // internal so unit tests can drive recording state directly
        {
            if (!SetField(ref _isRecording, value))
                return;

            if (value)
            {
                // Starting (or resuming) recording must not retroactively include whatever
                // segment the pipeline was silently mid-flight on while recording was off.
                _transcriptBuilder.DiscardInProgressSegment();
                _takeStartClockMs = _transcriptBuilder.ClockMs;
            }
        }
    }

    private double _takeStartClockMs;

    /// <summary>When the current take started, on the transcript's own time line (it can be
    /// negative: the transcript's zero is the session's first note). A note's
    /// <c>StartTimeMs</c> minus this is how far into the take it was sung, silence included.</summary>
    internal double TakeStartTranscriptMs => _takeStartClockMs - _transcriptBuilder.SessionOriginMs;

    public RelayCommand StartCommand { get; }
    public RelayCommand StopCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportMidiCommand { get; }
    public RelayCommand UndoLastEditCommand { get; }
    public RelayCommand StartListeningCommand { get; }
    public RelayCommand StopListeningCommand { get; }

    /// <summary>Attached by <see cref="MainViewModel"/> right after both view models are
    /// constructed - <see cref="PianoRollViewModel"/> itself depends on this class (constructor
    /// parameter), so the reverse pointer can only be set post-construction, not passed in here.
    /// Null in any test that constructs a bare <see cref="LiveTunerViewModel"/> without a Piano
    /// Roll attached - <see cref="RollTracks"/> degrades to "nothing to play/export" then,
    /// same as today's empty-transcript case.</summary>
    internal PianoRollViewModel? PianoRoll { get; set; }

    /// <summary>Export MIDI/JSON/CSV read the Piano Roll's tracks, edits and all, not the
    /// transcript (the roll's take track mirrors <see cref="Transcript"/> live).</summary>
    /// <summary>The Tempo box is the Live Tuner's only tempo (no auto-detection).</summary>
    private double RollBpm => PianoRoll?.Bpm ?? MusicalTime.DefaultBpm;

    /// <summary>Every track (exports write them all, whatever mute/solo say).</summary>
    private IReadOnlyList<TrackExport> RollTracks => PianoRoll?.ExportTracks() ?? Array.Empty<TrackExport>();

    public LiveTunerViewModel(
        PipelineSettingsViewModel settings,
        PlaybackViewModel playback,
        Func<IAudioCapture>? captureFactory = null,
        IAudioDeviceManager? deviceManager = null,
        VoicingDiagnosticsLogger? diagnosticsLogger = null)
    {
        _settings = settings;
        _captureFactory = captureFactory ?? (() => new WasapiAudioCapture());
        _deviceManager = deviceManager ?? new AudioDeviceManager();
        _dispatcher = Dispatcher.CurrentDispatcher;
        _diagnosticsLogger = diagnosticsLogger ?? new VoicingDiagnosticsLogger();
        _transcriptBuilder = new LiveTranscriptBuilder(_settings.MinSilenceMs, _settings.MaxVoicedGapMs);

        _diagnosticsFlushTimer = new System.Threading.Timer(
            _ => _diagnosticsLogger.Flush(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        TranscriptView = CollectionViewSource.GetDefaultView(Transcript);
        TranscriptView.Filter = o => !HideSilenceRows || ((TranscriptEntryViewModel)o).Type != "Silence";

        foreach (MMDevice device in _deviceManager.GetCaptureDevices())
            CaptureDevices.Add(device);
        _deviceManager.CaptureDevicesChanged += OnCaptureDevicesChanged;

        StartCommand = new RelayCommand(_ => { IsRecording = true; RaiseCommandsCanExecuteChanged(); }, _ => IsPipelineRunning && !IsRecording);
        StopCommand = new RelayCommand(_ => { IsRecording = false; RaiseCommandsCanExecuteChanged(); }, _ => IsRecording);
        ClearCommand = new RelayCommand(_ => Clear());
        ExportJsonCommand = new RelayCommand(_ => ExportJson(), _ => PianoRoll?.HasAnyNotes == true);
        ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => PianoRoll?.HasAnyNotes == true);
        ExportMidiCommand = new RelayCommand(_ => ExportMidi(), _ => PianoRoll?.HasAnyNotes == true);
        UndoLastEditCommand = new RelayCommand(_ => UndoLastEdit(), _ => CanUndoEdit);
        StartListeningCommand = new RelayCommand(_ => StartListening(), _ => !IsPipelineRunning && SelectedDevice is not null);
        StopListeningCommand = new RelayCommand(_ => StopListening(), _ => IsPipelineRunning);

        SelectedDevice = CaptureDevices.FirstOrDefault(); // auto-starts the pipeline via the setter, if a device is available
    }

    private void RestartPipelineForDeviceChange()
    {
        if (IsPipelineRunning)
            StopPipeline();
        if (_selectedDevice is not null && !IsListeningStoppedByUser)
            StartPipeline();
        RaiseCommandsCanExecuteChanged();
    }

    private void StartListening()
    {
        IsListeningStoppedByUser = false;
        if (!IsPipelineRunning && _selectedDevice is not null)
            StartPipeline();
        RaiseCommandsCanExecuteChanged();
    }

    /// <summary>Stops capture and detection until Start listening. Leaves <see cref="IsRecording"/>
    /// as it is, the same as when a device disappears mid-recording.</summary>
    private void StopListening()
    {
        IsListeningStoppedByUser = true;
        if (IsPipelineRunning)
            StopPipeline();
        RaiseCommandsCanExecuteChanged();
    }

    /// <summary>Called (via the dispatcher) whenever Windows reports a capture endpoint added,
    /// removed, or changing state — e.g. a USB mic's cable coming loose and being plugged back
    /// in. Refreshes the device list and recovers automatically: if the previously selected
    /// device disappeared, tears down the now-dead capture stream (falling back to another
    /// device if one exists); if it reappears while the pipeline is stopped, resumes on it.</summary>
    private void OnCaptureDevicesChanged() => _dispatcher.BeginInvoke(RefreshCaptureDevices);

    private void RefreshCaptureDevices()
    {
        string? previouslySelectedId = _selectedDevice?.ID;
        IReadOnlyList<MMDevice> updatedDevices = _deviceManager.GetCaptureDevices();

        CaptureDevices.Clear();
        foreach (MMDevice device in updatedDevices)
            CaptureDevices.Add(device);
        OnPropertyChanged(nameof(HasNoDevices));

        MMDevice? stillPresent = previouslySelectedId is null
            ? null
            : updatedDevices.FirstOrDefault(d => d.ID == previouslySelectedId);

        if (stillPresent is null)
        {
            // The selected device is gone (unplugged, disabled, or a driver fault). Tear down
            // the now-dead capture stream and fall back to whatever else is available, if anything.
            if (IsPipelineRunning)
                StopPipeline();
            _selectedDevice = updatedDevices.FirstOrDefault();
            OnPropertyChanged(nameof(SelectedDevice));
            RaiseDeviceChanged();
            if (_selectedDevice is not null && !IsListeningStoppedByUser)
                StartPipeline();
            RaiseCommandsCanExecuteChanged();
            return;
        }

        // Keep pointing at the live MMDevice instance either way (the old reference may now be stale).
        _selectedDevice = stillPresent;
        OnPropertyChanged(nameof(SelectedDevice));
        RaiseDeviceChanged();

        if (!IsPipelineRunning && !IsListeningStoppedByUser)
        {
            // The device is back (e.g. cable replugged after a disconnect) — resume automatically
            // rather than leaving the user stuck on a dead pipeline with no obvious next step.
            StartPipeline();
            RaiseCommandsCanExecuteChanged();
        }
    }

    /// <summary>Called (via the dispatcher) when the capture stream stops unexpectedly (not from
    /// our own <see cref="StopPipeline"/> call, which unsubscribes this first) — most commonly a
    /// device disconnecting mid-capture. Tears the pipeline down cleanly; recovery happens via
    /// <see cref="OnCaptureDevicesChanged"/> once Windows reports the device again.</summary>
    private void OnCaptureStoppedUnexpectedly() => _dispatcher.BeginInvoke(() =>
    {
        if (IsPipelineRunning)
            StopPipeline();
    });

    private void StartPipeline()
    {
        _capture = _captureFactory();

        // Wire up the accumulator and every event handler *before* starting capture, so no
        // sample chunk can arrive and be silently dropped by _accumulator being null (or a
        // completed frame lost to an as-yet-unsubscribed FrameReady handler) in the moment
        // between the capture device going live and this method finishing its setup.
        _accumulator = new SlidingWindowFrameAccumulator(WindowSize, HopSize);
        _accumulator.FrameReady += frame => OnFrameReady(frame, _capture!.SampleRate);
        _capture.SamplesAvailable += OnSamplesAvailable;
        _capture.CaptureStopped += OnCaptureStoppedUnexpectedly;

        _capture.Start(SelectedDevice);

        _settings.ApplyTo(_pipeline, _capture.SampleRate);
        _pipeline.Reset();
        _frameDurationMs = HopSize * 1000.0 / _capture.SampleRate;

        PitchHistory.Clear();
        _pitchClockMs = 0;
        SampleRateHz = _capture.SampleRate;
        IsPipelineRunning = true;
    }

    private void StopPipeline()
    {
        if (_capture is not null)
        {
            _capture.SamplesAvailable -= OnSamplesAvailable;
            _capture.CaptureStopped -= OnCaptureStoppedUnexpectedly;
            _capture.Dispose();
            _capture = null;
        }
        _accumulator = null;

        TranscriptEvent? trailing = _transcriptBuilder.Flush();
        if (trailing is not null && IsRecording)
            AppendCommittedEvent(trailing);

        IsPipelineRunning = false;
        CurrentPitch = null;
        CentsDeviation = 0;
        DetectedFrequencyHz = 0;
        IsVoiced = false;
        MeterBrush = UnvoicedBrush;
        NeedleOffsetPixels = 0;
        SampleRateHz = null;
        PitchHistory.Clear();
        RaisePitchReadoutsChanged();
    }

    private void Clear()
    {
        _transcriptBuilder.Reset();
        _committedEvents.Clear();
        Transcript.Clear();
        _lastEventEndMs = null;
        _keyDetector.Reset();
        SetKeyDisplayText("");
        CurrentVibratoRateHz = null;
        CurrentVibratoExtentCents = null;
        SetVibratoDisplayText("");
        _lastEditUndo = null;
        OnPropertyChanged(nameof(CanUndoEdit));
        RaiseCommandsCanExecuteChanged();
    }

    private void OnSamplesAvailable(float[] samples, int sampleRate) => _accumulator?.Write(samples);

    private void OnFrameReady(float[] frame, int sampleRate)
    {
        PitchAnalysisResult result = _pipeline.ProcessFrame(frame, sampleRate);

        // Cheap early-out: skip even formatting a line when logging is off, not just skip writing.
        if (_settings.DiagnosticLoggingEnabled)
        {
            _diagnosticsLogger.LogFrame(
                result.RmsEnergy, result.Aperiodicity,
                _settings.SilenceRmsThreshold, _settings.VoicingAperiodicityThreshold,
                result.IsVoiced);
        }

        _dispatcher.BeginInvoke(() => ProcessFrame(result));
    }

    /// <summary>Starts a fresh diagnostics log file the moment logging is switched on, so a new
    /// debugging session isn't buried at the bottom of a stale one.</summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PipelineSettingsViewModel.DiagnosticLoggingEnabled) && _settings.DiagnosticLoggingEnabled)
            _diagnosticsLogger.StartNewSession();

        // Applies immediately to the next fed frame - LiveTranscriptBuilder's in-flight segment
        // state doesn't depend on these thresholds until then, so no restart is needed.
        if (e.PropertyName == nameof(PipelineSettingsViewModel.MaxVoicedGapMs))
            _transcriptBuilder.MaxVoicedGapMs = _settings.MaxVoicedGapMs;
        if (e.PropertyName == nameof(PipelineSettingsViewModel.MinSilenceMs))
            _transcriptBuilder.MinSilenceMs = _settings.MinSilenceMs;
    }

    /// <summary>Runs one analysis result through the hero display (always) and, if currently
    /// recording, through the transcript builder. Exposed indirectly to tests via
    /// <see cref="SimulateFrameForTest"/> so ViewModel behavior can be verified without real
    /// audio capture or a pumped dispatcher.</summary>
    private void ProcessFrame(PitchAnalysisResult result)
    {
        IReadOnlyList<TranscriptEvent> committed = _transcriptBuilder.Feed(result, _frameDurationMs);
        ApplyResult(result);

        if (!IsRecording)
            return;

        foreach (TranscriptEvent evt in committed)
            AppendCommittedEvent(evt);
    }

    internal void SimulateFrameForTest(PitchAnalysisResult result) => ProcessFrame(result);

    /// <summary>Test-only: the frame length a running pipeline would set (tests have no capture device).</summary>
    internal double FrameDurationMsForTest
    {
        set => _frameDurationMs = value;
    }

    internal IReadOnlyList<TranscriptEvent> CommittedEventsForTest => _committedEvents;

    internal double? LastEventEndMsForTest => _lastEventEndMs;

    private void AppendCommittedEvent(TranscriptEvent evt)
    {
        double gapSincePrevMs = _lastEventEndMs is double lastEnd ? evt.StartTimeMs - lastEnd : 0.0;
        _lastEventEndMs = evt.StartTimeMs + evt.DurationMs;

        _committedEvents.Add(evt);
        Transcript.Add(TranscriptEntryViewModel.FromEvent(evt, gapSincePrevMs));

        if (evt.Type == TranscriptEventType.Note && evt.Note is { } note)
        {
            _keyDetector.AddNote(NoteQuantizer.GetPitchClass(note.MidiNumber), evt.DurationMs / 1000.0);
            DetectedKey? key = _keyDetector.Detect();
            SetKeyDisplayText(key is { } k ? $"{k.DisplayName} ({k.Confidence:P0})" : "");

            CurrentVibratoRateHz = note.Vibrato?.RateHz;
            CurrentVibratoExtentCents = note.Vibrato?.ExtentCents;
            SetVibratoDisplayText(note.Vibrato is { } vibrato
                ? $"{vibrato.RateHz:F1} Hz / ±{vibrato.ExtentCents:F0} cents"
                : "");
        }

        RaiseCommandsCanExecuteChanged();
    }

    private const double MinNoteDurationMs = 20.0;

    /// <summary>Scans backward from <paramref name="index"/> for the closest event in
    /// <see cref="_committedEvents"/> whose <c>StartTimeMs</c> genuinely precedes
    /// <paramref name="ownStartTimeMs"/> (skipping past any tied at the exact same instant - chord
    /// voices from <see cref="ApplyChordTransform"/>/<see cref="AddEntry"/> share one <c>StartTimeMs</c>
    /// across several consecutive indices) and returns that event's end time, or 0.0 if none. See
    /// <see cref="FindMaxEnd"/>'s doc comment for why the tie-skip matters.</summary>
    private double FindMinStart(int index, double ownStartTimeMs)
    {
        int i = index - 1;
        while (i >= 0 && _committedEvents[i].StartTimeMs == ownStartTimeMs)
            i--;
        return i >= 0 ? _committedEvents[i].StartTimeMs + _committedEvents[i].DurationMs : 0.0;
    }

    /// <summary>Scans forward from <paramref name="index"/> for the closest event whose
    /// <c>StartTimeMs</c> genuinely follows <paramref name="ownStartTimeMs"/> (skipping past any tied
    /// at the exact same instant) and returns that event's own start, or <see cref="double.MaxValue"/>
    /// if none. Without the tie-skip, editing/inserting a note that has a same-start sibling (any
    /// chord voice, or a brand-new note drawn onto an existing chord's time slot) would clamp against
    /// that sibling's own start/end - both of which equal this note's own start - collapsing the
    /// allowed length down to nothing regardless of how far the user actually dragged, since
    /// simultaneous voices are meant to overlap freely, not block each other.</summary>
    private double FindMaxEnd(int index, double ownStartTimeMs)
    {
        int i = index;
        while (i < _committedEvents.Count && _committedEvents[i].StartTimeMs == ownStartTimeMs)
            i++;
        return i < _committedEvents.Count ? _committedEvents[i].StartTimeMs : double.MaxValue;
    }

    /// <summary>Same backward scan as <see cref="FindMinStart"/> but returns the neighbor's own index
    /// (or -1 if none) instead of its end time - <see cref="MoveOrResizeEntry"/>'s cascading shrink
    /// needs to mutate that neighbor, not just read a boundary value from it.</summary>
    private int FindDistinctPrevIndex(int index, double ownStartTimeMs)
    {
        int i = index - 1;
        while (i >= 0 && _committedEvents[i].StartTimeMs == ownStartTimeMs)
            i--;
        return i;
    }

    /// <summary>Same forward scan as <see cref="FindMaxEnd"/> but returns the neighbor's own index
    /// (or -1 if none) instead of its start time.</summary>
    private int FindDistinctNextIndex(int index, double ownStartTimeMs)
    {
        int i = index;
        while (i < _committedEvents.Count && _committedEvents[i].StartTimeMs == ownStartTimeMs)
            i++;
        return i < _committedEvents.Count ? i : -1;
    }

    /// <summary>Replaces the committed event/row at <paramref name="index"/> with
    /// <paramref name="newEvent"/>, keeping <see cref="_committedEvents"/> and <see cref="Transcript"/>
    /// in lockstep - the shared RemoveAt+Insert pattern used throughout this class's edit methods.</summary>
    private void CommitEventAt(int index, TranscriptEvent newEvent)
    {
        _committedEvents[index] = newEvent;
        double gap = ComputeGapSincePrev(index);
        TranscriptEntryViewModel newRow = TranscriptEntryViewModel.FromEvent(newEvent, gap);
        Transcript.RemoveAt(index);
        Transcript.Insert(index, newRow);
    }

    private List<EditUndoState>? _lastEditUndo;

    /// <summary>True after a chord transform, delete, move/resize, or pitch-change has been
    /// applied and not yet undone. Single-level: applying a new edit (or Clear) discards the
    /// ability to undo the previous one.</summary>
    public bool CanUndoEdit => _lastEditUndo is not null;

    /// <summary>Independently replaces each of the given notes with a chord voicing rooted on its
    /// own original pitch (per PHASE2-FIXES.md Change 7 — this is N independent transforms, not
    /// one chord spanning all selected roots). <paramref name="selectedEntries"/> and
    /// <see cref="Transcript"/>/<see cref="_committedEvents"/> are matched by reference, since
    /// <see cref="TranscriptEntryViewModel"/> has no value identity. Returns the newly created
    /// chord-voice rows (all of them, across every transformed note) so the caller — the Piano
    /// Roll, which owns selection state — can re-select them.</summary>
    public IReadOnlyList<TranscriptEntryViewModel> ApplyChordTransform(
        IReadOnlyList<TranscriptEntryViewModel> selectedEntries, ChordQuality quality)
    {
        // Highest index first: replacing a later note doesn't shift the index of an earlier one
        // still waiting to be processed, so each recorded index stays valid until its own turn.
        List<int> targetIndexes = selectedEntries
            .Select(entry => Transcript.IndexOf(entry))
            .Where(index => index >= 0)
            .Distinct()
            .OrderByDescending(index => index)
            .ToList();

        var undoStates = new List<EditUndoState>();
        var newSelection = new List<TranscriptEntryViewModel>();

        foreach (int index in targetIndexes)
        {
            TranscriptEvent originalEvent = _committedEvents[index];
            TranscriptEntryViewModel originalRow = Transcript[index];
            if (originalEvent.Type != TranscriptEventType.Note || originalEvent.Note is not { } note)
                continue;

            Guid chordGroupId = Guid.NewGuid();
            int[] chordMidiNotes = ChordBuilder.BuildChord(note.MidiNumber, quality);

            _committedEvents.RemoveAt(index);
            Transcript.RemoveAt(index);

            for (int voice = 0; voice < chordMidiNotes.Length; voice++)
            {
                TranscriptEvent voiceEvent = originalEvent with { Note = note with { MidiNumber = chordMidiNotes[voice], ChordGroupId = chordGroupId } };
                TranscriptEntryViewModel voiceRow = TranscriptEntryViewModel.FromEvent(
                    voiceEvent, voice == 0 ? originalRow.GapSincePrevMs : 0.0);

                _committedEvents.Insert(index + voice, voiceEvent);
                Transcript.Insert(index + voice, voiceRow);
                newSelection.Add(voiceRow);
            }

            undoStates.Add(new EditUndoState(index, originalEvent, originalRow, chordMidiNotes.Length));
        }

        if (undoStates.Count == 0)
            return newSelection;

        _lastEditUndo = undoStates;
        OnPropertyChanged(nameof(CanUndoEdit));
        RaiseCommandsCanExecuteChanged();
        return newSelection;
    }

    /// <summary>Removes the given entries (e.g. Piano Roll Delete key, or the Grid's right-click
    /// "Delete row(s)") — highest-index-first, same rationale as <see cref="ApplyChordTransform"/>.
    /// Gated on <see cref="IsRecording"/>: bars are being added/pruned concurrently while live, so
    /// an edit committing against a stale index would be a real hazard.</summary>
    public void DeleteEntries(IReadOnlyList<TranscriptEntryViewModel> entries)
    {
        if (IsRecording)
            return;

        List<int> indexes = entries
            .Select(entry => Transcript.IndexOf(entry))
            .Where(index => index >= 0)
            .Distinct()
            .OrderByDescending(index => index)
            .ToList();
        if (indexes.Count == 0)
            return;

        var undoStates = new List<EditUndoState>();
        foreach (int index in indexes)
        {
            undoStates.Add(new EditUndoState(index, _committedEvents[index], Transcript[index], VoiceCount: 1));
            _committedEvents.RemoveAt(index);
            Transcript.RemoveAt(index);
            RefreshGapSincePrev(index); // whatever now sits here (previously index+1) may need its gap updated
        }

        _lastEditUndo = undoStates;
        OnPropertyChanged(nameof(CanUndoEdit));
        RaiseCommandsCanExecuteChanged();
        RecomputeLastEventEndMs();
    }

    /// <summary>Moves and/or resizes a single note (Grid tab Start/Duration cell edits are the only
    /// remaining caller since Phase 5 repointed the Piano Roll's own edits at
    /// <c>PianoRollViewModel</c>). Growing past a neighbor — Note or Silence, no distinction, since
    /// <see cref="TranscriptEvent"/> is a uniform shape across both types — auto-shrinks that
    /// neighbor instead of rejecting the edit, down to the same <see cref="MinNoteDurationMs"/> floor
    /// used everywhere else in this file (no separate "minimum Silence duration" concept exists).
    /// This keeps the list always time-ordered without needing to re-sort/reorder on every edit, which
    /// the gap-since-prev math, export, and playback scheduling all otherwise assume. "Neighbor" skips
    /// past any tied on the exact same <c>StartTimeMs</c> (see <see cref="FindMaxEnd"/>) — chord
    /// voices from <see cref="ApplyChordTransform"/> all share one StartTimeMs across several
    /// consecutive indices, and are meant to overlap each other freely, not block or shrink each
    /// other. If growth exhausts the neighbor down to its own floor, the edit clamps there rather than
    /// growing further (same as the old behavior, just with the neighbor shrunk as far as it safely
    /// can be first). Returns the replacement row (for the caller to re-select, since
    /// <see cref="TranscriptEntryViewModel"/> has no value identity and this always builds a new
    /// instance) or null if <paramref name="entry"/> isn't found.</summary>
    public TranscriptEntryViewModel? MoveOrResizeEntry(TranscriptEntryViewModel entry, double newStartTimeMs, double newDurationMs)
    {
        if (IsRecording)
            return null;

        int index = Transcript.IndexOf(entry);
        if (index < 0)
            return null;

        TranscriptEvent originalEvent = _committedEvents[index];
        TranscriptEntryViewModel originalRow = Transcript[index];
        double ownStartTimeMs = originalEvent.StartTimeMs;
        double minStart = FindMinStart(index, ownStartTimeMs);
        double maxEnd = FindMaxEnd(index, ownStartTimeMs);

        newStartTimeMs = Math.Max(0.0, newStartTimeMs);
        newDurationMs = Math.Max(MinNoteDurationMs, newDurationMs);

        var undoStates = new List<EditUndoState> { new(index, originalEvent, originalRow, VoiceCount: 1) };

        // Left side: shrinking the start so it reaches into the previous neighbor shrinks that
        // neighbor's duration (its own start stays put) instead of clamping the edit back.
        if (newStartTimeMs < minStart)
        {
            int prevIndex = FindDistinctPrevIndex(index, ownStartTimeMs);
            if (prevIndex >= 0)
            {
                TranscriptEvent prevEvent = _committedEvents[prevIndex];
                newStartTimeMs = Math.Max(newStartTimeMs, prevEvent.StartTimeMs + MinNoteDurationMs);

                undoStates.Add(new EditUndoState(prevIndex, prevEvent, Transcript[prevIndex], VoiceCount: 1));
                CommitEventAt(prevIndex, prevEvent with { DurationMs = newStartTimeMs - prevEvent.StartTimeMs });
            }
        }

        // Right side: growing the end so it reaches into the next neighbor shrinks+shifts that
        // neighbor (its own end stays put) instead of clamping the edit back.
        double requestedEndMs = newStartTimeMs + newDurationMs;
        if (requestedEndMs > maxEnd)
        {
            int nextIndex = FindDistinctNextIndex(index, ownStartTimeMs);
            if (nextIndex >= 0)
            {
                TranscriptEvent nextEvent = _committedEvents[nextIndex];
                double nextEndMs = nextEvent.StartTimeMs + nextEvent.DurationMs;
                requestedEndMs = Math.Min(requestedEndMs, nextEndMs - MinNoteDurationMs);

                undoStates.Add(new EditUndoState(nextIndex, nextEvent, Transcript[nextIndex], VoiceCount: 1));
                CommitEventAt(nextIndex, nextEvent with { StartTimeMs = requestedEndMs, DurationMs = nextEndMs - requestedEndMs });
                RefreshGapSincePrev(nextIndex + 1); // the shrunk neighbor's own end moved, so whatever follows it needs its gap refreshed too
            }
            newDurationMs = requestedEndMs - newStartTimeMs;
        }

        _lastEditUndo = undoStates;
        OnPropertyChanged(nameof(CanUndoEdit));
        RaiseCommandsCanExecuteChanged();

        CommitEventAt(index, originalEvent with { StartTimeMs = newStartTimeMs, DurationMs = newDurationMs });

        RefreshGapSincePrev(index + 1); // the next row's gap-from-this-note may have changed
        RecomputeLastEventEndMs();
        return Transcript[index];
    }

    /// <summary>Moves a single note in time AND pitch together, atomically (one undo step) - the
    /// Piano Roll's drag-the-note-body gesture changes both at once (standard DAW convention), so
    /// unlike <see cref="MoveOrResizeEntry"/> (used for the resize handle, and for the Grid's
    /// independent Start/Duration cell edits) this can't be composed from two separate single-field
    /// edits without losing one of them from the single-level undo stack. Duration is preserved
    /// (a move, not a resize); clamped the same way as <see cref="MoveOrResizeEntry"/> so the note
    /// can't be dragged past an adjacent neighbor.</summary>
    public TranscriptEntryViewModel? MoveEntry(TranscriptEntryViewModel entry, double newStartTimeMs, int newMidiNumber)
    {
        if (IsRecording)
            return null;

        int index = Transcript.IndexOf(entry);
        if (index < 0 || _committedEvents[index].Note is not { } note)
            return null;

        double durationMs = _committedEvents[index].DurationMs;
        double ownStartTimeMs = _committedEvents[index].StartTimeMs;
        double minStart = FindMinStart(index, ownStartTimeMs);
        double maxEnd = FindMaxEnd(index, ownStartTimeMs);
        newStartTimeMs = Math.Clamp(newStartTimeMs, minStart, Math.Max(minStart, maxEnd - durationMs));

        TranscriptEvent originalEvent = _committedEvents[index];
        TranscriptEntryViewModel originalRow = Transcript[index];
        TranscriptEvent newEvent = originalEvent with
        {
            StartTimeMs = newStartTimeMs,
            Note = note with { MidiNumber = newMidiNumber, CentsDeviation = 0.0 },
        };

        _lastEditUndo = new List<EditUndoState> { new(index, originalEvent, originalRow, VoiceCount: 1) };
        OnPropertyChanged(nameof(CanUndoEdit));
        RaiseCommandsCanExecuteChanged();

        _committedEvents[index] = newEvent;
        double gap = ComputeGapSincePrev(index);
        TranscriptEntryViewModel newRow = TranscriptEntryViewModel.FromEvent(newEvent, gap);
        Transcript.RemoveAt(index);
        Transcript.Insert(index, newRow);

        RefreshGapSincePrev(index + 1);
        RecomputeLastEventEndMs();
        return newRow;
    }

    /// <summary>Changes a single note's pitch (e.g. Grid Note/Octave cell edit) without touching
    /// its timing. Resets <c>CentsDeviation</c> to 0 — a
    /// manual pitch correction means "this is the correct note," so the stale detection-time
    /// deviation from the original (wrong) pitch is no longer meaningful.</summary>
    public TranscriptEntryViewModel? ChangeEntryPitch(TranscriptEntryViewModel entry, int newMidiNumber)
    {
        if (IsRecording)
            return null;

        int index = Transcript.IndexOf(entry);
        if (index < 0 || _committedEvents[index].Note is not { } note)
            return null;

        TranscriptEvent originalEvent = _committedEvents[index];
        TranscriptEntryViewModel originalRow = Transcript[index];
        TranscriptEvent newEvent = originalEvent with { Note = note with { MidiNumber = newMidiNumber, CentsDeviation = 0.0 } };

        _lastEditUndo = new List<EditUndoState> { new(index, originalEvent, originalRow, VoiceCount: 1) };
        OnPropertyChanged(nameof(CanUndoEdit));
        RaiseCommandsCanExecuteChanged();

        _committedEvents[index] = newEvent;
        TranscriptEntryViewModel newRow = TranscriptEntryViewModel.FromEvent(newEvent, entry.GapSincePrevMs);
        Transcript.RemoveAt(index);
        Transcript.Insert(index, newRow);
        return newRow;
    }

    /// <summary>Draws a brand-new note (Piano Roll click-and-drag on empty background). Clamped to
    /// whatever events already sit on either side of it at <paramref name="startTimeMs"/>, same
    /// rationale/limitation as <see cref="MoveOrResizeEntry"/> (can't be drawn overlapping a
    /// neighbor, Note or Silence). <c>ConfidenceMean</c> is set to 1.0 and cents deviation to 0 -
    /// a manually drawn note wasn't detected, so there's no real confidence/tuning-accuracy to
    /// report. Not wired into <see cref="UndoLastEdit"/>: the shared undo stack's shape ("restore
    /// this original event/row at this index") has nothing to restore for a fresh insert; delete the
    /// new note directly (select it, press Delete) to undo a draw instead - a disclosed v1
    /// limitation, not an oversight.</summary>
    public TranscriptEntryViewModel? AddEntry(double startTimeMs, double durationMs, int midiNumber)
    {
        if (IsRecording)
            return null;

        // >= (not just >): a tie against an existing event's own start must insert BEFORE that
        // event, not skip past it - otherwise clamping below would key off the wrong neighbor.
        int index = _committedEvents.FindIndex(e => e.StartTimeMs >= startTimeMs);
        if (index < 0)
            index = _committedEvents.Count;

        double minStart = FindMinStart(index, startTimeMs);
        double maxEnd = FindMaxEnd(index, startTimeMs);

        startTimeMs = Math.Clamp(startTimeMs, minStart, Math.Max(minStart, maxEnd - MinNoteDurationMs));
        durationMs = Math.Clamp(durationMs, MinNoteDurationMs, Math.Max(MinNoteDurationMs, maxEnd - startTimeMs));

        var noteInfo = new NoteInfo(midiNumber, CentsDeviation: 0.0,
            MeanF0Hz: NoteQuantizer.AbsoluteCentsToFrequency(midiNumber * 100.0), ConfidenceMean: 1.0);
        var newEvent = new TranscriptEvent(TranscriptEventType.Note, startTimeMs, durationMs, noteInfo);

        _committedEvents.Insert(index, newEvent);
        double gap = ComputeGapSincePrev(index);
        TranscriptEntryViewModel newRow = TranscriptEntryViewModel.FromEvent(newEvent, gap);
        Transcript.Insert(index, newRow);

        RefreshGapSincePrev(index + 1); // the following row's gap-from-this-new-note may have changed
        RecomputeLastEventEndMs();
        return newRow;
    }

    private void UndoLastEdit()
    {
        if (_lastEditUndo is not { } undoStates)
            return;

        // Reverse of apply order (which was high-to-low): undoing the lowest index first
        // collapses its inserted voices back to one note, which shifts everything above it left
        // by exactly the amount that insertion originally shifted it right — restoring every
        // higher recorded index to correctness before its own turn comes up.
        for (int i = undoStates.Count - 1; i >= 0; i--)
        {
            EditUndoState state = undoStates[i];
            for (int voice = 0; voice < state.VoiceCount; voice++)
            {
                _committedEvents.RemoveAt(state.Index);
                Transcript.RemoveAt(state.Index);
            }

            _committedEvents.Insert(state.Index, state.OriginalEvent);
            Transcript.Insert(state.Index, state.OriginalRow);
        }

        _lastEditUndo = null;
        OnPropertyChanged(nameof(CanUndoEdit));
        RaiseCommandsCanExecuteChanged();
        RecomputeLastEventEndMs();
    }

    private readonly record struct EditUndoState(
        int Index, TranscriptEvent OriginalEvent, TranscriptEntryViewModel OriginalRow, int VoiceCount);

    /// <summary>Gap (ms) between <see cref="_committedEvents"/>'s entry at <paramref name="index"/>
    /// and whatever ends immediately before it - 0 if it's the first event.</summary>
    private double ComputeGapSincePrev(int index)
    {
        if (index <= 0 || index >= _committedEvents.Count)
            return 0.0;
        TranscriptEvent prev = _committedEvents[index - 1];
        return _committedEvents[index].StartTimeMs - (prev.StartTimeMs + prev.DurationMs);
    }

    /// <summary>Rebuilds just the display row at <paramref name="index"/> with a freshly computed
    /// gap-since-prev, without touching <see cref="_committedEvents"/> (its own event data is
    /// unchanged - only its neighbor moved/was removed). A no-op past the end of the list.</summary>
    private void RefreshGapSincePrev(int index)
    {
        if (index < 0 || index >= _committedEvents.Count)
            return;
        double gap = ComputeGapSincePrev(index);
        TranscriptEntryViewModel refreshedRow = TranscriptEntryViewModel.FromEvent(_committedEvents[index], gap);
        Transcript.RemoveAt(index);
        Transcript.Insert(index, refreshedRow);
    }

    /// <summary>Keeps the live-append gap-since-prev anchor correct after an edit touches the tail
    /// of <see cref="_committedEvents"/> - otherwise resuming recording after editing the last
    /// note would compute its next gap against a stale/deleted/moved event.</summary>
    private void RecomputeLastEventEndMs()
    {
        _lastEventEndMs = _committedEvents.Count > 0
            ? _committedEvents[^1].StartTimeMs + _committedEvents[^1].DurationMs
            : null;
    }

    private void ApplyResult(PitchAnalysisResult result)
    {
        IsVoiced = result.IsVoiced;

        if (result.IsVoiced)
        {
            (string name, int octave) = NoteQuantizer.GetNoteNameAndOctave(result.MidiNote);
            CurrentPitch = new PitchDisplay(name, octave);
            CentsDeviation = result.CentsDeviation;
            DetectedFrequencyHz = result.SmoothedF0Hz;

            double absCents = Math.Abs(result.CentsDeviation);
            MeterBrush = absCents < 5 ? InTuneBrush : absCents < 15 ? CloseBrush : OffPitchBrush;
        }
        else
        {
            CurrentPitch = null;
            CentsDeviation = 0;
            DetectedFrequencyHz = 0;
            MeterBrush = UnvoicedBrush;
        }

        NeedleOffsetPixels = Math.Clamp(CentsDeviation, -50.0, 50.0) * 2.0;

        PitchHistory.Add(_pitchClockMs, result.IsVoiced ? result.CentsDeviation : null);
        _pitchClockMs += _frameDurationMs;
        RaisePitchReadoutsChanged();

        LogHeroState();
    }

    /// <summary>Permanent troubleshooting hook for the hero display (kept from the Bug 5
    /// investigation): confirms whether DisplayNote/HeroOpacity are computing as expected,
    /// which narrows a future "hero shows nothing" report to rendering vs. computation before
    /// any code changes are made.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void LogHeroState()
    {
        System.Diagnostics.Debug.WriteLine(
            $"[HERO] DisplayNote='{DisplayNote}' IsPipelineRunning={IsPipelineRunning} " +
            $"CurrentPitch={(CurrentPitch is { } p ? $"{p.Note}{p.Octave}" : "null")} HeroOpacity={HeroOpacity}");
    }

    private void ExportJson()
    {
        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "live-transcript.json" };
        if (dialog.ShowDialog() == true)
            TranscriptExporter.ExportJson(RollTracks, dialog.FileName, RollBpm);
    }

    private void ExportCsv()
    {
        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "live-transcript.csv" };
        if (dialog.ShowDialog() == true)
            TranscriptExporter.ExportCsv(RollTracks, dialog.FileName, RollBpm);
    }

    private void ExportMidi()
    {
        var dialog = new SaveFileDialog { Filter = "MIDI (*.mid)|*.mid", FileName = "live-transcript.mid" };
        if (dialog.ShowDialog() != true)
            return;

        MidiExporter.Export(RollTracks, dialog.FileName, RollBpm);
    }

    private void RaiseCommandsCanExecuteChanged() => System.Windows.Input.CommandManager.InvalidateRequerySuggested();

    public void Dispose()
    {
        _deviceManager.CaptureDevicesChanged -= OnCaptureDevicesChanged;
        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        StopPipeline();
        if (_deviceManager is IDisposable disposableDeviceManager)
            disposableDeviceManager.Dispose();
        _diagnosticsFlushTimer.Dispose();
        _diagnosticsLogger.Flush();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
