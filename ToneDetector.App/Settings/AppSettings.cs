using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Settings;

/// <summary>
/// Plain, JSON-serializable snapshot of every user-tunable setting exposed on the Settings tab —
/// deliberately a separate type from the ViewModels (which also carry commands, scan results, and
/// other non-persistable UI state), matching each ViewModel property's own default so a
/// missing/partial settings file round-trips to the same defaults the app already shipped with.
/// Excludes <c>SelectedMidiDevice</c>: a MIDI device index/name from a previous session isn't a
/// reliable identifier for "the same device" after a restart (reordering, unplugging), so USB MIDI
/// selection intentionally resets each launch rather than risk silently binding to the wrong port.
/// </summary>
public sealed class AppSettings
{
    // PipelineSettingsViewModel
    public bool UseCustomSampleRate { get; set; }
    public int CustomSampleRateHz { get; set; } = 22050;
    public bool EnableHighPass { get; set; } = true;
    public double HighPassCutoffHz { get; set; } = 80.0;
    public bool EnableLowPass { get; set; }
    public double LowPassCutoffHz { get; set; } = 4000.0;
    public double VoicingAperiodicityThreshold { get; set; } = 0.20;
    public double SilenceRmsThreshold { get; set; } = 0.005;
    public double MaxVoicedGapMs { get; set; } = 110.0;
    public double MinSilenceMs { get; set; } = 150.0;
    public bool DiagnosticLoggingEnabled { get; set; }
    public string SmoothingMode { get; set; } = "Kalman";
    public int MovingAverageWindowSize { get; set; } = 5;
    public string KalmanPreset { get; set; } = "Smooth";

    // VisualizationSettingsViewModel
    /// <summary>Piano roll time zoom per tab (pixels per beat, Alt+wheel).</summary>
    public double LiveTunerPixelsPerBeat { get; set; } = 48.0;
    public double FileAnalysisPixelsPerBeat { get; set; } = 48.0;
    public bool PianoRollColorByPitchClass { get; set; } = true;
    public bool NotationEnabled { get; set; }
    public int NotationUpdateThrottleMs { get; set; } = 500;
    public double LiveTunerBottomPanelHeight { get; set; } = 320.0;
    public bool LiveTunerEditorMaximized { get; set; }

    // Piano roll Grid menu, per tab (GridDivision names; unknown values fall back to 1/16)
    public string LiveTunerGrid { get; set; } = nameof(GridDivision.Sixteenth);
    public string FileAnalysisGrid { get; set; } = nameof(GridDivision.Sixteenth);

    // Tuner panel per tab: open or collapsed, and its height
    public bool LiveTunerTunerExpanded { get; set; } = true;
    public double LiveTunerTunerHeight { get; set; } = ViewModels.VisualizationSettingsViewModel.DefaultTunerHeight;
    public bool FileAnalysisTunerExpanded { get; set; }
    public double FileAnalysisTunerHeight { get; set; } = ViewModels.VisualizationSettingsViewModel.DefaultTunerHeight;

    // NoteInputSettingsViewModel
    public int KeyboardBaseOctave { get; set; } = 48;
    public int VoiceCount { get; set; } = 16;
    public double MasterVolume { get; set; } = 1.0;
    public string SynthEngine { get; set; } = ViewModels.NoteInputSettingsViewModel.BuiltInEngineName;
    public string? SelectedPluginPath { get; set; }
    public string AdditionalScanPath { get; set; } = "";
    public List<string> HiddenPluginPaths { get; set; } = new();
}
