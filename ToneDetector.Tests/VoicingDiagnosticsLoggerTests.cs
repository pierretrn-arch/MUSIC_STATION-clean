using System.IO;
using ToneDetector.Core.Diagnostics;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Tests <see cref="VoicingDiagnosticsLogger"/> against a temp file, calling
/// <see cref="VoicingDiagnosticsLogger.Flush"/> directly rather than waiting on a timer, for
/// deterministic assertions.</summary>
public sealed class VoicingDiagnosticsLoggerTests : IDisposable
{
    private readonly string _filePath;
    private readonly VoicingDiagnosticsLogger _logger;

    public VoicingDiagnosticsLoggerTests()
    {
        _filePath = Path.Combine(Path.GetTempPath(), "VoicingDiagnosticsLoggerTests_" + Guid.NewGuid() + ".log");
        _logger = new VoicingDiagnosticsLogger(_filePath);
    }

    public void Dispose()
    {
        if (File.Exists(_filePath))
            File.Delete(_filePath);
    }

    [Fact]
    public void LogFrame_ThenFlush_WritesALineWithAllMeasurements()
    {
        _logger.LogFrame(rms: 0.123, aperiodicity: 0.05, minSignalLevel: 0.005, breathinessTolerance: 0.2, isVoiced: true);
        _logger.Flush();

        string content = File.ReadAllText(_filePath);
        Assert.Contains("rms=0.12300", content);
        Assert.Contains("aperiodicity=0.05000", content);
        Assert.Contains("Min. signal level=0.00500", content);
        Assert.Contains("Breathiness tolerance=0.20000", content);
        Assert.Contains("voiced=True", content);
        Assert.Contains("[accepted]", content);
    }

    [Fact]
    public void LogFrame_RejectedByMinSignalLevel_ReportsAperiodicityAsNotComputed()
    {
        // Mirrors YinPitchDetector.DetectPitch's early-return path: below the RMS gate, the pitch
        // search never runs, so the "aperiodicity" the caller has is a fixed 1.0 placeholder, not
        // a real measurement - the log must say so explicitly rather than presenting it as real.
        _logger.LogFrame(rms: 0.001, aperiodicity: 1.0, minSignalLevel: 0.005, breathinessTolerance: 0.2, isVoiced: false);
        _logger.Flush();

        string content = File.ReadAllText(_filePath);
        Assert.Contains("aperiodicity=n/a (rejected by Min. signal level before pitch search ran)", content);
        Assert.Contains("[rejected: below Min. signal level]", content);
        Assert.DoesNotContain("aperiodicity=1.00000", content);
    }

    [Fact]
    public void LogFrame_RejectedByBreathinessTolerance_ReportsRealAperiodicityAndCorrectReason()
    {
        _logger.LogFrame(rms: 0.05, aperiodicity: 0.35, minSignalLevel: 0.005, breathinessTolerance: 0.2, isVoiced: false);
        _logger.Flush();

        string content = File.ReadAllText(_filePath);
        Assert.Contains("rms=0.05000", content);
        Assert.Contains("aperiodicity=0.35000", content);
        Assert.Contains("[rejected: above Breathiness tolerance]", content);
    }

    [Fact]
    public void Flush_WithNothingEnqueued_DoesNotCreateFile()
    {
        _logger.Flush();

        Assert.False(File.Exists(_filePath));
    }

    [Fact]
    public void MultipleLogFrameCalls_ThenOneFlush_WritesAllLinesInOrder()
    {
        _logger.LogFrame(0.01, 0.1, 0.005, 0.2, true);
        _logger.LogFrame(0.02, 0.2, 0.005, 0.2, false);
        _logger.LogFrame(0.03, 0.3, 0.005, 0.2, false);
        _logger.Flush();

        string[] lines = File.ReadAllLines(_filePath);
        Assert.Equal(3, lines.Length);
        Assert.Contains("rms=0.01000", lines[0]);
        Assert.Contains("rms=0.02000", lines[1]);
        Assert.Contains("rms=0.03000", lines[2]);
    }

    [Fact]
    public void StartNewSession_TruncatesPreviousContentAndWritesBanner()
    {
        _logger.LogFrame(0.01, 0.1, 0.005, 0.2, true);
        _logger.Flush();
        Assert.Contains("rms=0.01000", File.ReadAllText(_filePath));

        _logger.StartNewSession();

        string content = File.ReadAllText(_filePath);
        Assert.DoesNotContain("rms=0.01000", content);
        Assert.Contains("Voicing diagnostics started", content);
    }
}
