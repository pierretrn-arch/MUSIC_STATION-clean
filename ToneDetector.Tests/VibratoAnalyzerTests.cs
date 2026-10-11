using ToneDetector.Core.Analysis;
using Xunit;

namespace ToneDetector.Tests;

public class VibratoAnalyzerTests
{
    [Fact]
    public void Analyze_On5HzSineWith20CentsAmplitude_ReturnsExpectedRateAndExtent()
    {
        const double frequencyHz = 5.0;
        const double amplitudeCents = 20.0;
        const double durationMs = 500.0;
        const double sampleIntervalSeconds = 0.002; // 2ms -> 250 samples over 500ms

        // Small phase offset avoids sample points landing exactly on a zero crossing, where
        // floating-point noise could otherwise register as a spurious extra crossing.
        const double phaseOffset = 0.3;

        var series = new List<double>();
        for (double t = 0; t < durationMs / 1000.0; t += sampleIntervalSeconds)
            series.Add(amplitudeCents * Math.Sin(2.0 * Math.PI * frequencyHz * t + phaseOffset));

        VibratoAnalysis? result = VibratoAnalyzer.Analyze(series, durationMs);

        Assert.NotNull(result);
        Assert.InRange(result!.Value.RateHz, 4.5, 5.5);
        Assert.InRange(result.Value.ExtentCents, 18.0, 22.0);
    }

    [Fact]
    public void Analyze_OnNoteShorterThan400Ms_ReturnsNull()
    {
        var series = new List<double> { -10, 10, -10, 10, -10, 10 };

        VibratoAnalysis? result = VibratoAnalyzer.Analyze(series, durationMs: 200.0);

        Assert.Null(result);
    }

    [Fact]
    public void Analyze_OnSteadyNote_ReturnsNull()
    {
        var series = Enumerable.Repeat(0.0, 50).ToList();

        VibratoAnalysis? result = VibratoAnalyzer.Analyze(series, durationMs: 500.0);

        Assert.Null(result);
    }
}
