using System.IO;
using ToneDetector.App.Settings;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Tests <see cref="SettingsStore"/> against a temporary file path (the internal
/// path-parameterized overloads), never touching the real user's AppData settings file.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _filePath;

    public SettingsStoreTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), "SettingsStoreTests_" + Guid.NewGuid() + ".json");
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        AppSettings loaded = SettingsStore.Load(_filePath);

        var defaults = new AppSettings();
        Assert.Equal(defaults.HighPassCutoffHz, loaded.HighPassCutoffHz);
        Assert.Equal(defaults.VoicingAperiodicityThreshold, loaded.VoicingAperiodicityThreshold);
        Assert.Equal(defaults.SmoothingMode, loaded.SmoothingMode);
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsWithoutThrowing()
    {
        File.WriteAllText(_filePath, "{ this is not valid json");

        AppSettings loaded = SettingsStore.Load(_filePath);

        Assert.Equal(new AppSettings().HighPassCutoffHz, loaded.HighPassCutoffHz);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAllValues()
    {
        var original = new AppSettings
        {
            UseCustomSampleRate = true,
            CustomSampleRateHz = 44100,
            EnableHighPass = false,
            HighPassCutoffHz = 123.5,
            EnableLowPass = true,
            LowPassCutoffHz = 3210.0,
            VoicingAperiodicityThreshold = 0.001,
            SilenceRmsThreshold = 0.0123,
            MaxVoicedGapMs = 95.0,
            MinSilenceMs = 175.0,
            DiagnosticLoggingEnabled = true,
            SmoothingMode = "MovingAverage",
            MovingAverageWindowSize = 7,
            KalmanPreset = "Studio",
            LiveTunerPixelsPerBeat = 64.0,
            FileAnalysisPixelsPerBeat = 30.5,
            PianoRollColorByPitchClass = false,
            NotationEnabled = true,
            NotationUpdateThrottleMs = 250,
            LiveTunerBottomPanelHeight = 275.0,
            LiveTunerEditorMaximized = true,
            KeyboardBaseOctave = 60,
            VoiceCount = 8,
            MasterVolume = 0.75,
            SynthEngine = "VST3 Plugin",
            SelectedPluginPath = @"C:\Program Files\Common Files\VST3\Some Vendor\Some Synth.vst3",
            AdditionalScanPath = @"D:\MyPlugins",
            HiddenPluginPaths = new List<string> { @"C:\Program Files\Common Files\VST3\iZotope\iZotope BreakTweaker.vst3" },
        };

        SettingsStore.Save(original, _filePath);
        AppSettings loaded = SettingsStore.Load(_filePath);

        Assert.Equal(original.UseCustomSampleRate, loaded.UseCustomSampleRate);
        Assert.Equal(original.CustomSampleRateHz, loaded.CustomSampleRateHz);
        Assert.Equal(original.EnableHighPass, loaded.EnableHighPass);
        Assert.Equal(original.HighPassCutoffHz, loaded.HighPassCutoffHz);
        Assert.Equal(original.EnableLowPass, loaded.EnableLowPass);
        Assert.Equal(original.LowPassCutoffHz, loaded.LowPassCutoffHz);
        Assert.Equal(original.VoicingAperiodicityThreshold, loaded.VoicingAperiodicityThreshold);
        Assert.Equal(original.SilenceRmsThreshold, loaded.SilenceRmsThreshold);
        Assert.Equal(original.MaxVoicedGapMs, loaded.MaxVoicedGapMs);
        Assert.Equal(original.MinSilenceMs, loaded.MinSilenceMs);
        Assert.Equal(original.DiagnosticLoggingEnabled, loaded.DiagnosticLoggingEnabled);
        Assert.Equal(original.SmoothingMode, loaded.SmoothingMode);
        Assert.Equal(original.MovingAverageWindowSize, loaded.MovingAverageWindowSize);
        Assert.Equal(original.KalmanPreset, loaded.KalmanPreset);
        Assert.Equal(original.LiveTunerPixelsPerBeat, loaded.LiveTunerPixelsPerBeat);
        Assert.Equal(original.FileAnalysisPixelsPerBeat, loaded.FileAnalysisPixelsPerBeat);
        Assert.Equal(original.PianoRollColorByPitchClass, loaded.PianoRollColorByPitchClass);
        Assert.Equal(original.NotationEnabled, loaded.NotationEnabled);
        Assert.Equal(original.NotationUpdateThrottleMs, loaded.NotationUpdateThrottleMs);
        Assert.Equal(original.LiveTunerBottomPanelHeight, loaded.LiveTunerBottomPanelHeight);
        Assert.Equal(original.LiveTunerEditorMaximized, loaded.LiveTunerEditorMaximized);
        Assert.Equal(original.KeyboardBaseOctave, loaded.KeyboardBaseOctave);
        Assert.Equal(original.VoiceCount, loaded.VoiceCount);
        Assert.Equal(original.MasterVolume, loaded.MasterVolume);
        Assert.Equal(original.SynthEngine, loaded.SynthEngine);
        Assert.Equal(original.SelectedPluginPath, loaded.SelectedPluginPath);
        Assert.Equal(original.AdditionalScanPath, loaded.AdditionalScanPath);
        Assert.Equal(original.HiddenPluginPaths, loaded.HiddenPluginPaths);
    }

    [Fact]
    public void Save_CreatesParentDirectoryIfMissing()
    {
        string nestedPath = Path.Combine(Path.GetTempPath(), "SettingsStoreTests_" + Guid.NewGuid(), "sub", "settings.json");
        try
        {
            SettingsStore.Save(new AppSettings(), nestedPath);

            Assert.True(File.Exists(nestedPath));
        }
        finally
        {
            string? topDir = Path.GetDirectoryName(Path.GetDirectoryName(nestedPath));
            if (topDir is not null && Directory.Exists(topDir))
                Directory.Delete(topDir, recursive: true);
        }
    }
}
