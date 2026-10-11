using System.Windows.Threading;
using NAudio.CoreAudioApi;
using ToneDetector.App.ViewModels;
using ToneDetector.Audio;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>
/// Covers Start/Stop listening on the Home screen: stopping sets a session-only flag that keeps
/// device changes (Windows events or picking a new input) from restarting the pipeline, while the
/// selected device is still tracked and used on the next Start. Like the hotplug tests, these need
/// one real MMDevice instance to hand the fake device manager, and skip when none is available.
/// </summary>
public class LiveTunerViewModelListeningTests
{
    [Fact]
    public void OnLaunch_ListensByDefault()
    {
        if (CreateWithRealDevice() is not { } h)
            return; // no capture device available on this machine to drive the test
        var viewModel = h.ViewModel;

        Assert.True(viewModel.IsPipelineRunning);
        Assert.False(viewModel.IsListeningStoppedByUser);
        Assert.True(viewModel.StopListeningCommand.CanExecute(null));
        Assert.False(viewModel.StartListeningCommand.CanExecute(null));
    }

    [Fact]
    public void StopListening_StopsThePipelineAndSetsTheFlag()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var viewModel = h.ViewModel;
        var capture = h.Capture;

        viewModel.StopListeningCommand.Execute(null);

        Assert.False(viewModel.IsPipelineRunning);
        Assert.True(viewModel.IsListeningStoppedByUser);
        Assert.True(capture.DisposeCallCount > 0);
        Assert.Null(viewModel.SampleRateHz);
        Assert.Empty(viewModel.PitchHistory.Samples);
        Assert.True(viewModel.StartListeningCommand.CanExecute(null));
        Assert.False(viewModel.StopListeningCommand.CanExecute(null));
    }

    [Fact]
    public void StartListening_AfterStop_ResumesAndClearsTheFlag()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var viewModel = h.ViewModel;
        var capture = h.Capture;
        var device = h.Device;
        viewModel.StopListeningCommand.Execute(null);
        int startsBefore = capture.StartCallCount;

        viewModel.StartListeningCommand.Execute(null);

        Assert.True(viewModel.IsPipelineRunning);
        Assert.False(viewModel.IsListeningStoppedByUser);
        Assert.Equal(startsBefore + 1, capture.StartCallCount);
        Assert.Same(device, capture.LastStartDevice);
    }

    [Fact]
    public void WhileStopped_WindowsDeviceEvents_DoNotRestartListening()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var viewModel = h.ViewModel;
        var capture = h.Capture;
        var device = h.Device;
        var deviceManager = h.DeviceManager;
        viewModel.StopListeningCommand.Execute(null);
        int startsBefore = capture.StartCallCount;

        // Cable comes loose, then goes back in.
        deviceManager.Devices = Array.Empty<MMDevice>();
        deviceManager.RaiseCaptureDevicesChanged();
        PumpDispatcher();
        deviceManager.Devices = new[] { device };
        deviceManager.RaiseCaptureDevicesChanged();
        PumpDispatcher();

        Assert.False(viewModel.IsPipelineRunning);
        Assert.True(viewModel.IsListeningStoppedByUser);
        Assert.Equal(startsBefore, capture.StartCallCount);
        Assert.Same(device, viewModel.SelectedDevice); // still tracked while stopped
    }

    [Fact]
    public void WhileStopped_WindowsDeviceEvents_RememberTheNewDeviceForStart()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var viewModel = h.ViewModel;
        var capture = h.Capture;
        var device = h.Device;
        var deviceManager = h.DeviceManager;
        viewModel.StopListeningCommand.Execute(null);

        deviceManager.Devices = Array.Empty<MMDevice>();
        deviceManager.RaiseCaptureDevicesChanged();
        PumpDispatcher();
        Assert.Null(viewModel.SelectedDevice);
        Assert.False(viewModel.StartListeningCommand.CanExecute(null)); // nothing to listen to

        deviceManager.Devices = new[] { device };
        deviceManager.RaiseCaptureDevicesChanged();
        PumpDispatcher();
        Assert.Same(device, viewModel.SelectedDevice);

        viewModel.StartListeningCommand.Execute(null);

        Assert.True(viewModel.IsPipelineRunning);
        Assert.Same(device, capture.LastStartDevice);
    }

    [Fact]
    public void WhileStopped_PickingAnInput_DoesNotRestartListening_AndStartUsesIt()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var viewModel = h.ViewModel;
        var capture = h.Capture;
        var device = h.Device;
        viewModel.StopListeningCommand.Execute(null);
        int startsBefore = capture.StartCallCount;

        // Two selection changes (away and back) both run the device-change path.
        viewModel.SelectedDevice = null;
        viewModel.SelectedDevice = device;

        Assert.False(viewModel.IsPipelineRunning);
        Assert.Equal(startsBefore, capture.StartCallCount);

        viewModel.StartListeningCommand.Execute(null);

        Assert.True(viewModel.IsPipelineRunning);
        Assert.Same(device, capture.LastStartDevice);
    }

    [Fact]
    public void WhileListening_PickingAnInput_StillRestartsAsBefore()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var viewModel = h.ViewModel;
        var capture = h.Capture;
        var device = h.Device;
        int startsBefore = capture.StartCallCount;

        viewModel.SelectedDevice = null;
        viewModel.SelectedDevice = device;

        Assert.True(viewModel.IsPipelineRunning);
        Assert.Equal(startsBefore + 1, capture.StartCallCount);
    }

    [Fact]
    public void Header_ShowsTheDeviceAndRateWhileListening_AndADashAfterStop()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var viewModel = h.ViewModel;

        Assert.True(viewModel.HasDevice);
        Assert.Equal(h.Device.FriendlyName, viewModel.DeviceName);
        Assert.Equal("48 kHz", viewModel.SampleRateText); // FakeAudioCapture runs at 48000

        viewModel.StopListeningCommand.Execute(null);

        Assert.Equal("— kHz", viewModel.SampleRateText);
        Assert.Equal(h.Device.FriendlyName, viewModel.DeviceName); // still shown while stopped
    }

    [Fact]
    public void StopListening_LeavesRecordingStateAlone()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var viewModel = h.ViewModel;
        viewModel.StartCommand.Execute(null);
        Assert.True(viewModel.IsRecording);

        viewModel.StopListeningCommand.Execute(null);

        Assert.True(viewModel.IsRecording); // same as a device disappearing mid-recording
    }

    [Fact]
    public void NewSession_ListensAgainEvenAfterAPreviousStop()
    {
        if (CreateWithRealDevice() is not { } h)
            return;
        var first = h.ViewModel;
        first.StopListeningCommand.Execute(null);
        first.Dispose();

        if (CreateWithRealDevice() is not { } h2)
            return;
        var second = h2.ViewModel;

        Assert.True(second.IsPipelineRunning); // the flag is not persisted
        Assert.False(second.IsListeningStoppedByUser);
    }

    private sealed record Harness(LiveTunerViewModel ViewModel, FakeAudioCapture Capture, MMDevice Device, FakeAudioDeviceManager DeviceManager);

    private static Harness? CreateWithRealDevice()
    {
        MMDevice? realDevice = GetRealDeviceOrNull();
        if (realDevice is null)
            return null;

        var capture = new FakeAudioCapture { SampleRate = 48000 };
        var deviceManager = new FakeAudioDeviceManager { Devices = new[] { realDevice } };
        var viewModel = new LiveTunerViewModel(
            new PipelineSettingsViewModel(),
            new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()),
            () => capture,
            deviceManager);
        return new Harness(viewModel, capture, realDevice, deviceManager);
    }

    private static MMDevice? GetRealDeviceOrNull()
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
