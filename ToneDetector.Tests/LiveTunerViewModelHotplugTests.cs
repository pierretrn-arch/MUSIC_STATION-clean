using System.Windows.Threading;
using NAudio.CoreAudioApi;
using ToneDetector.App.ViewModels;
using ToneDetector.Audio;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>
/// Covers recovering from a capture device disappearing and coming back — e.g. a USB
/// microphone's cable coming loose and being plugged back in. Two independent signals can
/// surface this: the capture stream itself stopping unexpectedly (<see cref="IAudioCapture.CaptureStopped"/>),
/// and Windows reporting the endpoint list changing (<see cref="IAudioDeviceManager.CaptureDevicesChanged"/>).
/// Both need to leave the ViewModel in a clean, auto-recovering state rather than stuck on a dead pipeline.
/// </summary>
public class LiveTunerViewModelHotplugTests
{
    [Fact]
    public void CaptureStoppingUnexpectedly_StopsThePipelineCleanly()
    {
        MMDevice? realDevice = GetRealDeviceOrSkip();
        if (realDevice is null)
            return; // no capture device available on this machine to drive the test

        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var deviceManager = new FakeAudioDeviceManager { Devices = new[] { realDevice } };
        var viewModel = new LiveTunerViewModel(new PipelineSettingsViewModel(), new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()), () => fakeCapture, deviceManager);
        Assert.True(viewModel.IsPipelineRunning);

        fakeCapture.RaiseCaptureStoppedUnexpectedly(); // e.g. AUDCLNT_E_DEVICE_INVALIDATED
        PumpDispatcher();

        Assert.False(viewModel.IsPipelineRunning);
        Assert.Null(viewModel.CurrentPitch);
    }

    [Fact]
    public void DeviceDisappearingThenReappearing_AutoRecoversThePipeline()
    {
        MMDevice? realDevice = GetRealDeviceOrSkip();
        if (realDevice is null)
            return; // no capture device available on this machine to drive the test

        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var deviceManager = new FakeAudioDeviceManager { Devices = new[] { realDevice } };
        var viewModel = new LiveTunerViewModel(new PipelineSettingsViewModel(), new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()), () => fakeCapture, deviceManager);
        Assert.True(viewModel.IsPipelineRunning);
        Assert.False(viewModel.HasNoDevices);

        // Simulate the cable coming loose: Windows reports the endpoint gone.
        deviceManager.Devices = Array.Empty<MMDevice>();
        deviceManager.RaiseCaptureDevicesChanged();
        PumpDispatcher();

        Assert.False(viewModel.IsPipelineRunning);
        Assert.True(viewModel.HasNoDevices);

        // Simulate plugging it back in: the same device reappears.
        deviceManager.Devices = new[] { realDevice };
        deviceManager.RaiseCaptureDevicesChanged();
        PumpDispatcher();

        Assert.True(viewModel.IsPipelineRunning);
        Assert.False(viewModel.HasNoDevices);
    }

    private static MMDevice? GetRealDeviceOrSkip()
    {
        using var enumerator = new AudioDeviceManager();
        return enumerator.GetCaptureDevices().FirstOrDefault();
    }

    private static void PumpDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
