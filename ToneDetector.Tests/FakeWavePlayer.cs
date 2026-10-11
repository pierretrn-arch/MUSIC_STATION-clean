using NAudio.Wave;

namespace ToneDetector.Tests;

/// <summary>Test double for <see cref="IWavePlayer"/> so <see cref="ToneDetector.App.ViewModels.PlaybackViewModel"/>
/// can be tested without touching a real audio output device.</summary>
public sealed class FakeWavePlayer : IWavePlayer
{
    public PlaybackState PlaybackState { get; private set; } = PlaybackState.Stopped;
    public float Volume { get; set; } = 1f;
    public WaveFormat? OutputWaveFormat { get; private set; }

    public event EventHandler<StoppedEventArgs>? PlaybackStopped;

    public void Init(IWaveProvider waveProvider) => OutputWaveFormat = waveProvider.WaveFormat;

    public void Play() => PlaybackState = PlaybackState.Playing;

    public void Stop()
    {
        PlaybackState = PlaybackState.Stopped;
        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
    }

    public void Pause() => PlaybackState = PlaybackState.Paused;

    public void Dispose() { }
}
