using ToneDetector.Audio.Synth;
using Xunit;

namespace ToneDetector.Tests;

public class SineSynthesizerTests
{
    private const int SampleRate = 48000;

    [Fact]
    public void NoteOn_ProducesNonSilentOutputAtApproximatelyCorrectFrequency()
    {
        var synth = new SineSynthesizer();
        synth.NoteOn(69, 127); // A4 = 440Hz

        float[] buffer = new float[SampleRate]; // 1 second, well past the 5ms attack
        synth.Output.Read(buffer, 0, buffer.Length);

        // Skip the attack ramp, then estimate frequency from zero-crossing rate over 0.5s.
        float[] steadyState = buffer[2000..26000];
        double estimatedHz = EstimateFrequencyByZeroCrossings(steadyState, SampleRate);

        Assert.True(steadyState.Any(s => Math.Abs(s) > 0.01), "Expected non-silent output while a note is held.");
        Assert.True(Math.Abs(estimatedHz - 440.0) < 5.0, $"Expected ~440Hz, estimated {estimatedHz:F1}Hz.");
    }

    [Fact]
    public void NoteOff_ReleasesThenGoesSilent()
    {
        var synth = new SineSynthesizer();
        synth.NoteOn(69, 127);

        float[] warmup = new float[1000];
        synth.Output.Read(warmup, 0, warmup.Length); // clear the attack

        synth.NoteOff(69);

        float[] duringRelease = new float[(int)(0.030 * SampleRate) + 100]; // ~30ms release window
        synth.Output.Read(duringRelease, 0, duringRelease.Length);

        float[] afterRelease = new float[1000];
        synth.Output.Read(afterRelease, 0, afterRelease.Length);

        Assert.All(afterRelease, s => Assert.Equal(0.0, s, precision: 3));
        Assert.Equal(0, synth.ActiveVoiceCountForTest);
    }

    [Fact]
    public void VoiceStealing_CapsActiveVoicesAtMaxAndDropsTheOldestNote()
    {
        var synth = new SineSynthesizer(maxVoices: 4);

        for (int i = 0; i < 4; i++)
            synth.NoteOn(60 + i, 100); // notes 60,61,62,63 fill all 4 voices

        Assert.Equal(4, synth.ActiveVoiceCountForTest);
        Assert.Contains(60, synth.ActiveMidiNotesForTest); // the oldest note is still active...

        synth.NoteOn(64, 100); // a 5th note must steal the oldest voice (60)

        Assert.Equal(4, synth.ActiveVoiceCountForTest); // still capped at maxVoices
        Assert.DoesNotContain(60, synth.ActiveMidiNotesForTest); // ...but not anymore
        Assert.Contains(64, synth.ActiveMidiNotesForTest);
    }

    [Fact]
    public void AllNotesOff_SilencesEverythingImmediately()
    {
        var synth = new SineSynthesizer();
        synth.NoteOn(60, 100);
        synth.NoteOn(64, 100);
        synth.NoteOn(67, 100);

        float[] warmup = new float[1000];
        synth.Output.Read(warmup, 0, warmup.Length);
        Assert.Equal(3, synth.ActiveVoiceCountForTest);

        synth.AllNotesOff();

        Assert.Equal(0, synth.ActiveVoiceCountForTest);
        float[] afterAllOff = new float[100];
        synth.Output.Read(afterAllOff, 0, afterAllOff.Length);
        Assert.All(afterAllOff, s => Assert.Equal(0.0f, s));
    }

    private static double EstimateFrequencyByZeroCrossings(float[] samples, int sampleRate)
    {
        int crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] <= 0 && samples[i] > 0)
                crossings++;
        }
        double durationSeconds = samples.Length / (double)sampleRate;
        return crossings / durationSeconds;
    }
}
