using ToneDetector.App.ViewModels;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Pitch;
using Xunit;

namespace ToneDetector.Tests;

public class PipelineSettingsViewModelTests
{
    [Fact]
    public void ApplyTo_ConfiguresVoicingThresholdsOnAFreshYinPitchDetector()
    {
        var settings = new PipelineSettingsViewModel
        {
            VoicingAperiodicityThreshold = 0.33,
            SilenceRmsThreshold = 0.002,
        };
        var pipeline = new PitchAnalysisPipeline();

        settings.ApplyTo(pipeline, nativeSampleRate: 48000);

        var detector = Assert.IsType<YinPitchDetector>(pipeline.PitchDetector);
        Assert.Equal(0.33, detector.AperiodicityThreshold);
        Assert.Equal(0.002, detector.RmsSilenceThreshold);
    }

    [Fact]
    public void DefaultThresholds_AreLooserThanYinPitchDetectorsOwnDefaults()
    {
        // These defaults exist specifically to give headroom for breathier/quieter higher-register
        // singing that would otherwise get rejected as unvoiced under YinPitchDetector's own
        // (stricter) built-in defaults.
        var settings = new PipelineSettingsViewModel();
        var bareDetectorDefaults = new YinPitchDetector();

        Assert.True(settings.VoicingAperiodicityThreshold > bareDetectorDefaults.AperiodicityThreshold);
        Assert.True(settings.SilenceRmsThreshold < bareDetectorDefaults.RmsSilenceThreshold);
    }
}
