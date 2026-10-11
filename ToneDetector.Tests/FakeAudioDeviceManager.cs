using NAudio.CoreAudioApi;
using ToneDetector.Audio;

namespace ToneDetector.Tests;

/// <summary>Test double for <see cref="IAudioDeviceManager"/> so hotplug (device
/// disconnect/reconnect) behavior can be driven deterministically without real Windows Core
/// Audio notifications. <see cref="Devices"/> is mutable and <see cref="RaiseCaptureDevicesChanged"/>
/// simulates a Windows notification firing.</summary>
public sealed class FakeAudioDeviceManager : IAudioDeviceManager
{
    public IReadOnlyList<MMDevice> Devices { get; set; } = Array.Empty<MMDevice>();

    public event Action? CaptureDevicesChanged;

    public IReadOnlyList<MMDevice> GetCaptureDevices() => Devices;

    public void RaiseCaptureDevicesChanged() => CaptureDevicesChanged?.Invoke();
}
