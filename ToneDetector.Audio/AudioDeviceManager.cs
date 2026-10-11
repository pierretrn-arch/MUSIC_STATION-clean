using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace ToneDetector.Audio;

/// <summary>
/// Enumerates Windows audio endpoints for the device-selection UI: microphone/line-in devices
/// for live capture, and render devices for optional loopback capture of system playback. Also
/// watches for capture-endpoint changes (plugged in, unplugged, enabled/disabled) via Windows'
/// Core Audio notification API, so the app can recover automatically — e.g. a USB mic whose cable
/// came loose and was plugged back in — instead of silently sitting on a dead capture stream.
/// </summary>
public sealed class AudioDeviceManager : IAudioDeviceManager, IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private bool _registered;

    /// <summary>Raised (on a background COM thread — marshal to the UI thread before touching
    /// bound collections) whenever a capture endpoint is added, removed, or changes state.</summary>
    public event Action? CaptureDevicesChanged;

    public AudioDeviceManager()
    {
        _enumerator.RegisterEndpointNotificationCallback(this);
        _registered = true;
    }

    public IReadOnlyList<MMDevice> GetCaptureDevices() =>
        _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

    public IReadOnlyList<MMDevice> GetRenderDevices() =>
        _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

    public MMDevice GetDefaultCaptureDevice() =>
        _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);

    public MMDevice GetDefaultRenderDevice() =>
        _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) => NotifyIfCaptureRelated(deviceId);

    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) => NotifyIfCaptureRelated(pwstrDeviceId);

    // The device is already gone by this point, so its DataFlow can't be looked up — refresh
    // unconditionally. This is cheap (a single endpoint enumeration) and correctness matters more
    // than skipping an occasional irrelevant (render-device) refresh.
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) => CaptureDevicesChanged?.Invoke();

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) { }

    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    private void NotifyIfCaptureRelated(string deviceId)
    {
        try
        {
            using MMDevice device = _enumerator.GetDevice(deviceId);
            if (device.DataFlow is DataFlow.Capture or DataFlow.All)
                CaptureDevicesChanged?.Invoke();
        }
        catch
        {
            // Device may have already disappeared again between the notification firing and this
            // lookup (e.g. rapid unplug/replug) — refresh unconditionally rather than risk missing
            // a real capture-device change.
            CaptureDevicesChanged?.Invoke();
        }
    }

    public void Dispose()
    {
        if (_registered)
        {
            _enumerator.UnregisterEndpointNotificationCallback(this);
            _registered = false;
        }
        _enumerator.Dispose();
    }
}
