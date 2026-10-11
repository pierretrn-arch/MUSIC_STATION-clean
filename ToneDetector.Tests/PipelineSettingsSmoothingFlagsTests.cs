using ToneDetector.App.ViewModels;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>The Settings screen enables "MA window size" and "Kalman preset" only for their own
/// smoothing mode, from these two flags.</summary>
public class PipelineSettingsSmoothingFlagsTests
{
    [Theory]
    [InlineData(SmoothingMode.None, false, false)]
    [InlineData(SmoothingMode.MovingAverage, true, false)]
    [InlineData(SmoothingMode.Kalman, false, true)]
    public void Flags_FollowTheSmoothingMode(SmoothingMode mode, bool movingAverage, bool kalman)
    {
        var settings = new PipelineSettingsViewModel { SmoothingMode = mode };

        Assert.Equal(movingAverage, settings.IsMovingAverageSelected);
        Assert.Equal(kalman, settings.IsKalmanSelected);
    }

    [Fact]
    public void ChangingTheMode_RaisesBothFlags()
    {
        var settings = new PipelineSettingsViewModel { SmoothingMode = SmoothingMode.Kalman };
        var raised = new List<string?>();
        settings.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        settings.SmoothingMode = SmoothingMode.MovingAverage;

        Assert.Contains(nameof(PipelineSettingsViewModel.SmoothingMode), raised);
        Assert.Contains(nameof(PipelineSettingsViewModel.IsMovingAverageSelected), raised);
        Assert.Contains(nameof(PipelineSettingsViewModel.IsKalmanSelected), raised);
    }

    [Fact]
    public void SettingTheSameMode_RaisesNothing()
    {
        var settings = new PipelineSettingsViewModel { SmoothingMode = SmoothingMode.Kalman };
        int raised = 0;
        settings.PropertyChanged += (_, _) => raised++;

        settings.SmoothingMode = SmoothingMode.Kalman;

        Assert.Equal(0, raised);
    }
}
