using NAudio.CoreAudioApi;

namespace ToneDetector.Audio;

/// <summary>
/// Abstraction over capture-device enumeration and change notification, so the ViewModel layer
/// can be unit-tested with a fake implementation instead of driving real Windows Core Audio.
/// </summary>
public interface IAudioDeviceManager
{
    /// <summary>Raised (on a background thread) whenever a capture endpoint is added, removed,
    /// or changes state — e.g. a USB mic being unplugged or plugged back in.</summary>
    event Action? CaptureDevicesChanged;

    IReadOnlyList<MMDevice> GetCaptureDevices();
}
