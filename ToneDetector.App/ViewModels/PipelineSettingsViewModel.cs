using System.ComponentModel;
using System.Runtime.CompilerServices;
using ToneDetector.Core.Dsp;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Pitch;

namespace ToneDetector.App.ViewModels;

public enum SmoothingMode
{
    None,
    MovingAverage,
    Kalman,
}

/// <summary>
/// Holds the user-configurable signal-processing settings (sample rate, filters, smoothing)
/// shared by both the live tuner and file-analysis views, and applies them to a
/// <see cref="PitchAnalysisPipeline"/> on demand. Both consumers re-read these settings each
/// time they (re)build their pipeline, so a change here takes effect on the next run.
/// </summary>
public sealed class PipelineSettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _useCustomSampleRate;
    public bool UseCustomSampleRate
    {
        get => _useCustomSampleRate;
        set => SetField(ref _useCustomSampleRate, value);
    }

    private int _customSampleRateHz = 22050;
    public int CustomSampleRateHz
    {
        get => _customSampleRateHz;
        set => SetField(ref _customSampleRateHz, value);
    }

    private bool _enableHighPass = true;
    public bool EnableHighPass
    {
        get => _enableHighPass;
        set => SetField(ref _enableHighPass, value);
    }

    private double _highPassCutoffHz = 80.0;
    public double HighPassCutoffHz
    {
        get => _highPassCutoffHz;
        set => SetField(ref _highPassCutoffHz, value);
    }

    private bool _enableLowPass;
    public bool EnableLowPass
    {
        get => _enableLowPass;
        set => SetField(ref _enableLowPass, value);
    }

    private double _lowPassCutoffHz = 4000.0;
    public double LowPassCutoffHz
    {
        get => _lowPassCutoffHz;
        set => SetField(ref _lowPassCutoffHz, value);
    }

    // Defaults loosened from YinPitchDetector's own (0.15 / 0.01): breathier or quieter higher-register
    // singing (e.g. male head voice/falsetto) can otherwise get rejected as "unvoiced" even though the
    // pitch itself is well within range — these give that headroom without giving up genuine silence/noise
    // rejection, and are exposed here so a specific voice/mic combination can be tuned further if needed.
    private double _voicingAperiodicityThreshold = 0.20;
    public double VoicingAperiodicityThreshold
    {
        get => _voicingAperiodicityThreshold;
        set => SetField(ref _voicingAperiodicityThreshold, value);
    }

    private double _silenceRmsThreshold = 0.005;
    public double SilenceRmsThreshold
    {
        get => _silenceRmsThreshold;
        set => SetField(ref _silenceRmsThreshold, value);
    }

    // Default raised from LiveTranscriptBuilder/TranscriptBuilder's own original 60ms: a brief
    // unvoiced dropout mid-note (vibrato dip, breath, momentary dropout) longer than 60ms but
    // shorter than MinSilenceMs below used to silently split one sustained note into two
    // fragments with no Silence row shown in between. 110ms gives headroom above a real observed
    // 85ms dropout that triggered exactly this bug, while staying well under MinSilenceMs so the
    // two thresholds don't fight each other in the gap between them.
    private double _maxVoicedGapMs = 110.0;
    public double MaxVoicedGapMs
    {
        get => _maxVoicedGapMs;
        set => SetField(ref _maxVoicedGapMs, value);
    }

    private double _minSilenceMs = 150.0;
    public double MinSilenceMs
    {
        get => _minSilenceMs;
        set => SetField(ref _minSilenceMs, value);
    }

    // DETECTION-DIAGNOSTICS-PRESETS.md Change 10: off by default - a deliberate, toggleable
    // debugging aid, not a permanent per-frame cost. See VoicingDiagnosticsLogger.
    private bool _diagnosticLoggingEnabled;
    public bool DiagnosticLoggingEnabled
    {
        get => _diagnosticLoggingEnabled;
        set => SetField(ref _diagnosticLoggingEnabled, value);
    }

    private SmoothingMode _smoothingMode = SmoothingMode.Kalman;
    public SmoothingMode SmoothingMode
    {
        get => _smoothingMode;
        set
        {
            if (_smoothingMode == value)
                return;
            SetField(ref _smoothingMode, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMovingAverageSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKalmanSelected)));
        }
    }

    /// <summary>Enables the MA window size setting (Settings shows it disabled otherwise).</summary>
    public bool IsMovingAverageSelected => SmoothingMode == SmoothingMode.MovingAverage;

    /// <summary>Enables the Kalman preset setting (Settings shows it disabled otherwise).</summary>
    public bool IsKalmanSelected => SmoothingMode == SmoothingMode.Kalman;

    private int _movingAverageWindowSize = 5;
    public int MovingAverageWindowSize
    {
        get => _movingAverageWindowSize;
        set => SetField(ref _movingAverageWindowSize, value);
    }

    private KalmanPitchSmoother.Preset _kalmanPreset = KalmanPitchSmoother.Preset.Smooth;
    public KalmanPitchSmoother.Preset KalmanPreset
    {
        get => _kalmanPreset;
        set => SetField(ref _kalmanPreset, value);
    }

    public IReadOnlyList<KalmanPitchSmoother.Preset> KalmanPresets { get; } =
        Enum.GetValues<KalmanPitchSmoother.Preset>();

    public IReadOnlyList<SmoothingMode> SmoothingModes { get; } = Enum.GetValues<SmoothingMode>();

    /// <summary>Applies the current settings to a pipeline's stage lists, target sample rate, and
    /// voicing/silence thresholds (rebuilding <see cref="PitchAnalysisPipeline.PitchDetector"/> as
    /// a fresh <see cref="YinPitchDetector"/>, since its threshold properties are init-only).</summary>
    public void ApplyTo(PitchAnalysisPipeline pipeline, int nativeSampleRate)
    {
        int effectiveSampleRate = UseCustomSampleRate ? CustomSampleRateHz : nativeSampleRate;

        pipeline.TargetSampleRate = UseCustomSampleRate ? CustomSampleRateHz : null;

        pipeline.PitchDetector = new YinPitchDetector
        {
            AperiodicityThreshold = VoicingAperiodicityThreshold,
            RmsSilenceThreshold = SilenceRmsThreshold,
        };

        pipeline.AudioStages.Clear();
        if (EnableHighPass)
            pipeline.AudioStages.Add(new HighPassFilter(HighPassCutoffHz, effectiveSampleRate));
        if (EnableLowPass)
            pipeline.AudioStages.Add(new LowPassFilter(LowPassCutoffHz, effectiveSampleRate));

        pipeline.PitchTrackStages.Clear();
        switch (SmoothingMode)
        {
            case SmoothingMode.MovingAverage:
                pipeline.PitchTrackStages.Add(new MovingAverageSmoother(MovingAverageWindowSize));
                break;
            case SmoothingMode.Kalman:
                pipeline.PitchTrackStages.Add(new KalmanPitchSmoother(KalmanPreset));
                break;
            case SmoothingMode.None:
            default:
                break;
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
