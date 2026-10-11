using ToneDetector.App.Settings;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>Root view model: holds the shared pipeline settings plus the live/file sub-viewmodels.</summary>
public sealed class MainViewModel : IDisposable
{
    public PipelineSettingsViewModel Settings { get; } = new();
    public VisualizationSettingsViewModel Visualization { get; } = new();
    public NoteInputSettingsViewModel NoteInput { get; } = new();
    public PlaybackViewModel Playback { get; } = new();
    public LiveTunerViewModel LiveTuner { get; }
    public FileAnalysisViewModel FileAnalysis { get; }
    public PianoRollViewModel PianoRoll { get; }
    public HomeViewModel Home { get; } = new();

    public MainViewModel()
    {
        LiveTuner = new LiveTunerViewModel(Settings, Playback);
        FileAnalysis = new FileAnalysisViewModel(Settings, Playback);
        PianoRoll = new PianoRollViewModel(Playback, LiveTuner);
        LiveTuner.PianoRoll = PianoRoll;
        Playback.InputRoll = PianoRoll;
        LoadSettings();
        NoteInput.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(NoteInputSettingsViewModel.SynthEngine) or nameof(NoteInputSettingsViewModel.SelectedPlugin))
                UpdateInstrumentName();
        };
        UpdateInstrumentName();
    }

    /// <summary>What every track plays through until per-track instruments: the Settings engine.</summary>
    public string InstrumentName =>
        NoteInput.SynthEngine == NoteInputSettingsViewModel.BuiltInEngineName || NoteInput.SelectedPlugin is null
            ? "Built-in synth"
            : NoteInput.SelectedPlugin.Name;

    private void UpdateInstrumentName()
    {
        PianoRoll.InstrumentName = InstrumentName;
        FileAnalysis.Roll.InstrumentName = InstrumentName;
    }

    /// <summary>True if either tab has notes in any track (closing loses them).</summary>
    public bool HasUnsavedTracks => PianoRoll.HasAnyNotes || FileAnalysis.Roll.HasAnyNotes;

    /// <summary>Applies whatever was saved from a previous session (or defaults, on first run/a
    /// missing/corrupt file) to the three Settings-tab ViewModels. Called once at startup, before
    /// anything reads their values (e.g. <see cref="LiveTunerViewModel"/>'s pipeline construction).</summary>
    private void LoadSettings()
    {
        AppSettings saved = SettingsStore.Load();

        Settings.UseCustomSampleRate = saved.UseCustomSampleRate;
        Settings.CustomSampleRateHz = saved.CustomSampleRateHz;
        Settings.EnableHighPass = saved.EnableHighPass;
        Settings.HighPassCutoffHz = saved.HighPassCutoffHz;
        Settings.EnableLowPass = saved.EnableLowPass;
        Settings.LowPassCutoffHz = saved.LowPassCutoffHz;
        Settings.VoicingAperiodicityThreshold = saved.VoicingAperiodicityThreshold;
        Settings.SilenceRmsThreshold = saved.SilenceRmsThreshold;
        Settings.MaxVoicedGapMs = saved.MaxVoicedGapMs;
        Settings.MinSilenceMs = saved.MinSilenceMs;
        Settings.DiagnosticLoggingEnabled = saved.DiagnosticLoggingEnabled;
        if (Enum.TryParse(saved.SmoothingMode, out SmoothingMode smoothingMode))
            Settings.SmoothingMode = smoothingMode;
        Settings.MovingAverageWindowSize = saved.MovingAverageWindowSize;
        if (Enum.TryParse(saved.KalmanPreset, out ToneDetector.Core.Dsp.KalmanPitchSmoother.Preset kalmanPreset))
            Settings.KalmanPreset = kalmanPreset;

        PianoRoll.PixelsPerBeat = saved.LiveTunerPixelsPerBeat;
        FileAnalysis.Roll.PixelsPerBeat = saved.FileAnalysisPixelsPerBeat;
        Visualization.PianoRollColorByPitchClass = saved.PianoRollColorByPitchClass;
        Visualization.NotationEnabled = saved.NotationEnabled;
        Visualization.NotationUpdateThrottleMs = saved.NotationUpdateThrottleMs;
        Visualization.LiveTunerBottomPanelHeight = saved.LiveTunerBottomPanelHeight;
        Visualization.LiveTunerEditorMaximized = saved.LiveTunerEditorMaximized;
        PianoRoll.Grid = GridDivisions.Parse(saved.LiveTunerGrid);
        Visualization.LiveTunerTunerExpanded = saved.LiveTunerTunerExpanded;
        Visualization.LiveTunerTunerHeight = saved.LiveTunerTunerHeight;
        Visualization.FileAnalysisTunerExpanded = saved.FileAnalysisTunerExpanded;
        Visualization.FileAnalysisTunerHeight = saved.FileAnalysisTunerHeight;
        FileAnalysis.Roll.Grid = GridDivisions.Parse(saved.FileAnalysisGrid);

        NoteInput.KeyboardBaseOctave = saved.KeyboardBaseOctave;
        NoteInput.VoiceCount = saved.VoiceCount;
        NoteInput.MasterVolume = saved.MasterVolume;
        NoteInput.SynthEngine = saved.SynthEngine;
        NoteInput.AdditionalScanPath = saved.AdditionalScanPath;
        NoteInput.SetHiddenPluginPaths(saved.HiddenPluginPaths);
        // AvailablePlugins was already populated by NoteInputSettingsViewModel's own constructor
        // (which scans only DefaultScanPath, before AdditionalScanPath/HiddenPluginPaths above were
        // known) - rescan now that both are in place, so a saved additional folder is actually
        // searched and a plugin the user removed last session stays gone. Then re-select the
        // previously-picked plugin by path, if it's still there.
        NoteInput.RescanPluginsCommand.Execute(null);
        if (saved.SelectedPluginPath is not null)
            NoteInput.SelectedPlugin = NoteInput.AvailablePlugins.FirstOrDefault(p => p.Path == saved.SelectedPluginPath);
    }

    /// <summary>Snapshots the three Settings-tab ViewModels' current values and writes them to
    /// disk. Called from <c>MainWindow</c>'s Closed handler so changes survive a restart.</summary>
    public void SaveSettings()
    {
        var settings = new AppSettings
        {
            UseCustomSampleRate = Settings.UseCustomSampleRate,
            CustomSampleRateHz = Settings.CustomSampleRateHz,
            EnableHighPass = Settings.EnableHighPass,
            HighPassCutoffHz = Settings.HighPassCutoffHz,
            EnableLowPass = Settings.EnableLowPass,
            LowPassCutoffHz = Settings.LowPassCutoffHz,
            VoicingAperiodicityThreshold = Settings.VoicingAperiodicityThreshold,
            SilenceRmsThreshold = Settings.SilenceRmsThreshold,
            MaxVoicedGapMs = Settings.MaxVoicedGapMs,
            MinSilenceMs = Settings.MinSilenceMs,
            DiagnosticLoggingEnabled = Settings.DiagnosticLoggingEnabled,
            SmoothingMode = Settings.SmoothingMode.ToString(),
            MovingAverageWindowSize = Settings.MovingAverageWindowSize,
            KalmanPreset = Settings.KalmanPreset.ToString(),

            LiveTunerPixelsPerBeat = PianoRoll.PixelsPerBeat,
            FileAnalysisPixelsPerBeat = FileAnalysis.Roll.PixelsPerBeat,
            PianoRollColorByPitchClass = Visualization.PianoRollColorByPitchClass,
            NotationEnabled = Visualization.NotationEnabled,
            NotationUpdateThrottleMs = Visualization.NotationUpdateThrottleMs,
            LiveTunerBottomPanelHeight = Visualization.LiveTunerBottomPanelHeight,
            LiveTunerEditorMaximized = Visualization.LiveTunerEditorMaximized,
            LiveTunerGrid = PianoRoll.Grid.ToString(),
            FileAnalysisGrid = FileAnalysis.Roll.Grid.ToString(),
            LiveTunerTunerExpanded = Visualization.LiveTunerTunerExpanded,
            LiveTunerTunerHeight = Visualization.LiveTunerTunerHeight,
            FileAnalysisTunerExpanded = Visualization.FileAnalysisTunerExpanded,
            FileAnalysisTunerHeight = Visualization.FileAnalysisTunerHeight,

            KeyboardBaseOctave = NoteInput.KeyboardBaseOctave,
            VoiceCount = NoteInput.VoiceCount,
            MasterVolume = NoteInput.MasterVolume,
            SynthEngine = NoteInput.SynthEngine,
            SelectedPluginPath = NoteInput.SelectedPlugin?.Path,
            AdditionalScanPath = NoteInput.AdditionalScanPath,
            HiddenPluginPaths = NoteInput.HiddenPluginPaths.ToList(),
        };

        SettingsStore.Save(settings);
    }

    public void Dispose()
    {
        LiveTuner.Dispose();
        Playback.Dispose();
    }
}
