using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ToneDetector.App.ViewModels;

/// <summary>
/// Phase 2 display settings (piano roll, notation) — separate from <see cref="PipelineSettingsViewModel"/>
/// since these configure how the transcript is *drawn*, not the signal-processing pipeline that
/// produces it. Bound by both the Settings tab and the Live Tuner tab's visualization controls.
/// </summary>
public sealed class VisualizationSettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _pianoRollColorByPitchClass = true;
    public bool PianoRollColorByPitchClass
    {
        get => _pianoRollColorByPitchClass;
        set => SetField(ref _pianoRollColorByPitchClass, value);
    }

    // Off by default: VexFlow rendering is expensive enough that leaving it running continuously
    // on every transcript update is a real perf cost most users won't want paid by default.
    private bool _notationEnabled;
    public bool NotationEnabled
    {
        get => _notationEnabled;
        set => SetField(ref _notationEnabled, value);
    }

    private int _notationUpdateThrottleMs = 500;
    public int NotationUpdateThrottleMs
    {
        get => _notationUpdateThrottleMs;
        set => SetField(ref _notationUpdateThrottleMs, value);
    }

    /// <summary>Height in pixels of the Live Tuner tab's bottom panel (Grid/Piano Roll/Notation),
    /// user-adjustable via a GridSplitter. Not exposed on the Settings tab — it's drag-driven,
    /// not a typed setting.</summary>
    private double _liveTunerBottomPanelHeight = 320.0;
    public double LiveTunerBottomPanelHeight
    {
        get => _liveTunerBottomPanelHeight;
        set => SetField(ref _liveTunerBottomPanelHeight, value);
    }

    /// <summary>Whether the Live Tuner tab's bottom panel (Grid/Piano Roll/Notation) is in its
    /// maximized-editor state — tuner display/status/keyboard hidden, panel filling all remaining
    /// space below the toolbar. Toggled via a button next to the splitter, not a typed setting.</summary>
    private bool _liveTunerEditorMaximized;
    public bool LiveTunerEditorMaximized
    {
        get => _liveTunerEditorMaximized;
        set => SetField(ref _liveTunerEditorMaximized, value);
    }

    /// <summary>Tuner panel under each tab's session bar: open or collapsed, and its height when
    /// open (drag handle). Open by default in Live Tuner, collapsed in File Analysis. Saved.</summary>
    public const double DefaultTunerHeight = 208.0;
    public const double MinTunerHeight = 176.0;
    public const double MaxTunerHeight = 420.0;

    private bool _liveTunerTunerExpanded = true;
    public bool LiveTunerTunerExpanded
    {
        get => _liveTunerTunerExpanded;
        set => SetField(ref _liveTunerTunerExpanded, value);
    }

    private double _liveTunerTunerHeight = DefaultTunerHeight;
    public double LiveTunerTunerHeight
    {
        get => _liveTunerTunerHeight;
        set => SetField(ref _liveTunerTunerHeight, ClampTunerHeight(value));
    }

    private bool _fileAnalysisTunerExpanded;
    public bool FileAnalysisTunerExpanded
    {
        get => _fileAnalysisTunerExpanded;
        set => SetField(ref _fileAnalysisTunerExpanded, value);
    }

    private double _fileAnalysisTunerHeight = DefaultTunerHeight;
    public double FileAnalysisTunerHeight
    {
        get => _fileAnalysisTunerHeight;
        set => SetField(ref _fileAnalysisTunerHeight, ClampTunerHeight(value));
    }

    internal static double ClampTunerHeight(double height) =>
        double.IsFinite(height) ? Math.Clamp(height, MinTunerHeight, MaxTunerHeight) : DefaultTunerHeight;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
