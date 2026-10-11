using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace ToneDetector.Audio;

/// <summary>
/// Live audio capture via WASAPI, downmixed to mono float samples for the analysis pipeline.
/// Supports either a microphone/line-in device or loopback capture of a render device (e.g. to
/// analyze whatever is currently playing on the system, not just a live mic/instrument).
/// </summary>
public sealed class WasapiAudioCapture : IAudioCapture
{
    private WasapiCapture? _capture;
    private BufferedWaveProvider? _bufferedWaveProvider;
    private ISampleProvider? _sampleProvider;
    private float[] _readBuffer = Array.Empty<float>();

    public int SampleRate { get; private set; }
    public bool IsCapturing { get; private set; }

    /// <summary>Raised on the capture thread with mono float samples and the capture sample rate.</summary>
    public event Action<float[], int>? SamplesAvailable;

    /// <summary>Raised whenever the capture stream stops, including unexpectedly (e.g. a USB
    /// mic's cable coming loose raises AUDCLNT_E_DEVICE_INVALIDATED via RecordingStopped).</summary>
    public event Action? CaptureStopped;

    public void Start(MMDevice? device = null, bool loopback = false)
    {
        if (IsCapturing)
            throw new InvalidOperationException("Capture is already running; call Stop() first.");

        _capture = loopback
            ? (device is null ? new WasapiLoopbackCapture() : new WasapiLoopbackCapture(device))
            : (device is null ? new WasapiCapture() : new WasapiCapture(device));

        WaveFormat format = _capture.WaveFormat;
        if (format.Channels > 2)
            throw new NotSupportedException($"Capture device has {format.Channels} channels; only mono or stereo input is supported.");

        SampleRate = format.SampleRate;

        _bufferedWaveProvider = new BufferedWaveProvider(format) { DiscardOnBufferOverflow = true };
        ISampleProvider sampleProvider = _bufferedWaveProvider.ToSampleProvider();
        _sampleProvider = format.Channels == 2
            ? new StereoToMonoSampleProvider(sampleProvider) { LeftVolume = 0.5f, RightVolume = 0.5f }
            : sampleProvider;

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += (_, _) =>
        {
            IsCapturing = false;
            CaptureStopped?.Invoke();
        };
        _capture.StartRecording();
        IsCapturing = true;
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        _bufferedWaveProvider!.AddSamples(e.Buffer, 0, e.BytesRecorded);

        int monoSampleCapacity = e.BytesRecorded / _capture!.WaveFormat.BlockAlign;
        if (_readBuffer.Length < monoSampleCapacity)
            _readBuffer = new float[monoSampleCapacity];

        int samplesRead = _sampleProvider!.Read(_readBuffer, 0, monoSampleCapacity);
        if (samplesRead > 0)
            SamplesAvailable?.Invoke(_readBuffer[..samplesRead], SampleRate);
    }

    public void Stop()
    {
        _capture?.StopRecording();
        IsCapturing = false;
    }

    public void Dispose()
    {
        Stop();
        _capture?.Dispose();
        _capture = null;
    }
}
