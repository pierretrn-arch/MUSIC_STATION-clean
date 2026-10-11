using ToneDetector.Audio.Synth;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>One built-in voice per track, each through its own pan and volume, summed to stereo.</summary>
public class TrackMixerTests
{
    private static (float Left, float Right) Peak(TrackMixer mixer)
    {
        var buffer = new float[2 * 4800];
        mixer.Read(buffer, 0, buffer.Length);
        float left = 0, right = 0;
        for (int i = 0; i < buffer.Length; i += 2)
        {
            left = Math.Max(left, Math.Abs(buffer[i]));
            right = Math.Max(right, Math.Abs(buffer[i + 1]));
        }
        return (left, right);
    }

    [Fact]
    public void OutputIsStereo_AndSilentWithNothingPlaying()
    {
        var mixer = new TrackMixer();
        Assert.Equal(2, mixer.WaveFormat.Channels);
        Assert.Equal((0f, 0f), Peak(mixer));
    }

    [Fact]
    public void EachTrack_GetsItsOwnVoice()
    {
        var mixer = new TrackMixer();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();

        Assert.NotSame(mixer.VoiceFor(a), mixer.VoiceFor(b));
        Assert.Same(mixer.VoiceFor(a), mixer.VoiceFor(a));
        Assert.Equal(2, mixer.TrackIds.Count);

        mixer.Remove(a);
        Assert.Equal(new[] { b }, mixer.TrackIds);
    }

    [Fact]
    public void CentrePan_IsFullLevelBothSides_HardLeftSilencesTheRight()
    {
        var mixer = new TrackMixer();
        Guid id = Guid.NewGuid();
        mixer.VoiceFor(id).NoteOn(69, 127);
        (float left, float right) = Peak(mixer);
        Assert.True(left > 0.5f);
        Assert.Equal(left, right, 3);

        mixer.SetMix(id, 1f, -1f);
        (float hardLeft, float silentRight) = Peak(mixer);
        Assert.True(hardLeft > 0.5f);
        Assert.Equal(0f, silentRight, 3);
    }

    [Fact]
    public void ZeroGain_IsSilent()
    {
        var mixer = new TrackMixer();
        Guid id = Guid.NewGuid();
        mixer.VoiceFor(id, gain: 0f).NoteOn(69, 127);
        Assert.Equal((0f, 0f), Peak(mixer));
    }
}
