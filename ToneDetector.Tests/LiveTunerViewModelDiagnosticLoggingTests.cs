using System.IO;
using System.Windows.Threading;
using ToneDetector.App.ViewModels;
using ToneDetector.Core.Diagnostics;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>
/// Confirms DETECTION-DIAGNOSTICS-PRESETS.md Change 10's diagnostic logging is actually wired into
/// the real per-frame path (<c>OnFrameReady</c>), not just present as unused code - drives audio
/// through the same <see cref="FakeAudioCapture"/>/<c>SamplesAvailable</c> route as
/// <see cref="LiveTunerViewModelEndToEndTests"/>, per that class's own rationale for why a
/// SimulateFrameForTest-style bypass wouldn't catch a wiring regression here either.
/// </summary>
public sealed class LiveTunerViewModelDiagnosticLoggingTests : IDisposable
{
    private readonly string _logFilePath;

    public LiveTunerViewModelDiagnosticLoggingTests()
    {
        _logFilePath = Path.Combine(Path.GetTempPath(), "LiveTunerDiagLogTests_" + Guid.NewGuid() + ".log");
    }

    public void Dispose()
    {
        if (File.Exists(_logFilePath))
            File.Delete(_logFilePath);
    }

    [Fact]
    public void DiagnosticLoggingEnabled_RealFramesProduceLogLinesWithCurrentThresholds()
    {
        var settings = new PipelineSettingsViewModel
        {
            DiagnosticLoggingEnabled = true,
            SilenceRmsThreshold = 0.005,
            VoicingAperiodicityThreshold = 0.2,
        };
        var logger = new VoicingDiagnosticsLogger(_logFilePath);
        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var viewModel = new LiveTunerViewModel(
            settings, new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()),
            () => fakeCapture, diagnosticsLogger: logger);

        float[] tone = GenerateSineWave(440.0, fakeCapture.SampleRate, 0.5);
        fakeCapture.RaiseSamplesAvailable(tone);
        PumpDispatcher();
        logger.Flush();

        Assert.True(File.Exists(_logFilePath));
        string content = File.ReadAllText(_logFilePath);
        Assert.Contains("Min. signal level=0.00500", content);
        Assert.Contains("Breathiness tolerance=0.20000", content);
        Assert.Contains("voiced=True", content); // a clean 440Hz tone should register as voiced

        viewModel.Dispose();
    }

    [Fact]
    public void DiagnosticLoggingDisabled_RealFramesProduceNoLogFile()
    {
        var settings = new PipelineSettingsViewModel { DiagnosticLoggingEnabled = false };
        var logger = new VoicingDiagnosticsLogger(_logFilePath);
        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var viewModel = new LiveTunerViewModel(
            settings, new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()),
            () => fakeCapture, diagnosticsLogger: logger);

        float[] tone = GenerateSineWave(440.0, fakeCapture.SampleRate, 0.5);
        fakeCapture.RaiseSamplesAvailable(tone);
        PumpDispatcher();
        logger.Flush();

        Assert.False(File.Exists(_logFilePath));

        viewModel.Dispose();
    }

    [Fact]
    public void TogglingDiagnosticLoggingOn_StartsAFreshSessionFile()
    {
        var settings = new PipelineSettingsViewModel { DiagnosticLoggingEnabled = false };
        var logger = new VoicingDiagnosticsLogger(_logFilePath);
        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var viewModel = new LiveTunerViewModel(
            settings, new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()),
            () => fakeCapture, diagnosticsLogger: logger);

        // Pre-existing stale content from an earlier session should not survive turning logging on.
        File.WriteAllText(_logFilePath, "stale content from a previous run\n");

        settings.DiagnosticLoggingEnabled = true;

        string content = File.ReadAllText(_logFilePath);
        Assert.DoesNotContain("stale content", content);
        Assert.Contains("Voicing diagnostics started", content);

        viewModel.Dispose();
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, double durationSeconds)
    {
        int sampleCount = (int)(sampleRate * durationSeconds);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate);
        return samples;
    }
}
