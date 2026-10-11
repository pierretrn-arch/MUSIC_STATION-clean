using NAudio.CoreAudioApi;
using ToneDetector.Audio;

namespace ToneDetector.Tests;

/// <summary>Test double for <see cref="IAudioCapture"/> so ViewModel tests can drive the
/// pipeline-start/pipeline-stop lifecycle without touching real WASAPI hardware.</summary>
public sealed class FakeAudioCapture : IAudioCapture
{
    public int SampleRate { get; set; } = 48000;
    public bool IsCapturing { get; private set; }
    public int StartCallCount { get; private set; }
    public int StopCallCount { get; private set; }
    public int DisposeCallCount { get; private set; }

    /// <summary>Device passed to the most recent <see cref="Start"/>.</summary>
    public MMDevice? LastStartDevice { get; private set; }

    public event Action<float[], int>? SamplesAvailable;
    public event Action? CaptureStopped;

    public void Start(MMDevice? device = null, bool loopback = false)
    {
        StartCallCount++;
        LastStartDevice = device;
        IsCapturing = true;
    }

    public void Stop()
    {
        StopCallCount++;
        IsCapturing = false;
    }

    public void Dispose()
    {
        DisposeCallCount++;
        IsCapturing = false;
    }

    /// <summary>Simulates the capture device delivering a chunk of mono samples.</summary>
    public void RaiseSamplesAvailable(float[] samples) => SamplesAvailable?.Invoke(samples, SampleRate);

    /// <summary>Simulates the device disappearing mid-capture (e.g. a USB cable coming loose).</summary>
    public void RaiseCaptureStoppedUnexpectedly()
    {
        IsCapturing = false;
        CaptureStopped?.Invoke();
    }
}
