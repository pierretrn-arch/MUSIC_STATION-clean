using NAudio.Wave;
using ToneDetector.Audio.Synth;
using Xunit;

namespace ToneDetector.Tests;

public class ClickSampleProviderTests
{
    /// <summary>A synth stand-in that outputs a constant level.</summary>
    private sealed class ConstantSource : ISampleProvider
    {
        private readonly float _value;
        public ConstantSource(float value, int sampleRate = 48000, int channels = 2)
        {
            _value = value;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
        }
        public WaveFormat WaveFormat { get; }
        public int Read(float[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
                buffer[offset + i] = _value;
            return count;
        }
    }

    [Fact]
    public void PassesTheSynthThrough_UntilTriggered()
    {
        var click = new ClickSampleProvider(new ConstantSource(0.25f));
        var buffer = new float[960];

        Assert.Equal(960, click.Read(buffer, 0, buffer.Length));
        Assert.All(buffer, s => Assert.Equal(0.25f, s));
    }

    [Fact]
    public void Trigger_AddsAShortTickOnEveryChannel_ThenGoesQuiet()
    {
        var click = new ClickSampleProvider(new ConstantSource(0f));
        click.Trigger(accent: true);

        var tick = new float[2 * 48000 * 35 / 1000]; // the whole 35 ms tick
        click.Read(tick, 0, tick.Length);
        Assert.Contains(tick, s => Math.Abs(s) > 0.1f);
        for (int i = 0; i < tick.Length; i += 2)
            Assert.Equal(tick[i], tick[i + 1]);

        var after = new float[960];
        click.Read(after, 0, after.Length);
        Assert.All(after, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void KeepsTheSynthsFormat()
    {
        var click = new ClickSampleProvider(new ConstantSource(0f, 44100, 1));
        Assert.Equal((44100, 1), (click.WaveFormat.SampleRate, click.WaveFormat.Channels));
    }
}
