using NAudio.CoreAudioApi;

namespace ToneDetector.Audio;

/// <summary>
/// Abstraction over live audio capture, so the ViewModel layer can be unit-tested with a fake
/// implementation instead of driving real WASAPI hardware.
/// </summary>
public interface IAudioCapture : IDisposable
{
    int SampleRate { get; }
    bool IsCapturing { get; }

    /// <summary>Raised with mono float samples and the capture sample rate.</summary>
    event Action<float[], int>? SamplesAvailable;

    /// <summary>Raised when the underlying capture stream stops for any reason, including
    /// unexpectedly (e.g. the device was unplugged mid-capture) — not just from an explicit
    /// <see cref="Stop"/>/<see cref="IDisposable.Dispose"/> call.</summary>
    event Action? CaptureStopped;

    void Start(MMDevice? device = null, bool loopback = false);
    void Stop();
}
