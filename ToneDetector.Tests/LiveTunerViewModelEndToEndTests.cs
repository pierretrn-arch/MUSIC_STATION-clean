using System.Windows.Threading;
using ToneDetector.App.ViewModels;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>
/// Exercises the real production data path end-to-end (FakeAudioCapture.SamplesAvailable →
/// SlidingWindowFrameAccumulator → PitchAnalysisPipeline → Dispatcher.BeginInvoke → ApplyResult),
/// rather than the SimulateFrameForTest bypass used in <see cref="LiveTunerViewModelTests"/>.
/// This is specifically to catch wiring regressions in StartPipeline/OnFrameReady (event
/// subscription order, dispatcher marshaling) that a bypass test can't see, since Change 3's
/// pipeline/recording split touched exactly that wiring.
/// </summary>
public class LiveTunerViewModelEndToEndTests
{
    [Fact]
    public void ConstructingViewModel_WithADevice_AutoStartsPipelineViaFakeCapture()
    {
        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var viewModel = new LiveTunerViewModel(new PipelineSettingsViewModel(), new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()), () => fakeCapture);

        if (viewModel.CaptureDevices.Count > 0)
        {
            Assert.True(viewModel.IsPipelineRunning);
            Assert.Equal(1, fakeCapture.StartCallCount);
        }
    }

    [Fact]
    public void RealCapturePath_PlayingACleanTone_UpdatesCurrentPitch()
    {
        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var viewModel = new LiveTunerViewModel(new PipelineSettingsViewModel(), new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()), () => fakeCapture);

        float[] tone = GenerateSineWave(440.0, fakeCapture.SampleRate, 0.5);
        fakeCapture.RaiseSamplesAvailable(tone);
        PumpDispatcher();

        Assert.NotNull(viewModel.CurrentPitch);
        Assert.Equal("A", viewModel.CurrentPitch!.Value.Note);
        Assert.Equal(4, viewModel.CurrentPitch.Value.Octave);
        Assert.Equal("A4", viewModel.DisplayNote.Replace("♯", "#").Replace("♭", "b"));
    }

    [Fact]
    public void RealCapturePath_PlayingACleanToneWhileRecording_PopulatesTranscriptOnceSilenceFollows()
    {
        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var viewModel = new LiveTunerViewModel(new PipelineSettingsViewModel(), new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()), () => fakeCapture);
        viewModel.IsRecording = true;

        float[] tone = GenerateSineWave(440.0, fakeCapture.SampleRate, 2.0);
        float[] silence = new float[fakeCapture.SampleRate]; // 1s, well past the min-silence threshold

        fakeCapture.RaiseSamplesAvailable(tone);
        PumpDispatcher();

        // Hero display should already reflect the tone before any silence/commit happens.
        Assert.NotNull(viewModel.CurrentPitch);
        Assert.Equal("A", viewModel.CurrentPitch!.Value.Note);

        fakeCapture.RaiseSamplesAvailable(silence);
        PumpDispatcher();

        TranscriptEntryViewModel row = Assert.Single(viewModel.Transcript);
        Assert.Equal("Note", row.Type);
        Assert.Equal("A", row.Note);
        Assert.Equal(4, row.Octave);
    }

    [Fact]
    public void RealCapturePath_PlayingACleanToneWhileNotRecording_UpdatesHeroButNotTranscript()
    {
        var fakeCapture = new FakeAudioCapture { SampleRate = 48000 };
        var viewModel = new LiveTunerViewModel(new PipelineSettingsViewModel(), new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()), () => fakeCapture);
        viewModel.IsRecording = false;

        float[] tone = GenerateSineWave(440.0, fakeCapture.SampleRate, 2.0);
        float[] silence = new float[fakeCapture.SampleRate];

        fakeCapture.RaiseSamplesAvailable(tone);
        PumpDispatcher();

        Assert.NotNull(viewModel.CurrentPitch); // hero always reflects the pipeline

        fakeCapture.RaiseSamplesAvailable(silence);
        PumpDispatcher();

        Assert.Empty(viewModel.Transcript); // but nothing is recorded while IsRecording is false
    }

    private static void PumpDispatcher()
    {
        // Standard "drain the queue" trick: queue a Background-priority action that stops the
        // frame, so everything queued at (higher) Normal priority — i.e. every BeginInvoke'd
        // ProcessFrame call from OnFrameReady — runs first. No message loop is active in a unit
        // test host, so without this, queued dispatcher operations would never execute.
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    private static float[] GenerateSineWave(double frequencyHz, int sampleRate, double durationSeconds)
    {
        int sampleCount = (int)(sampleRate * durationSeconds);
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
            samples[i] = (float)Math.Sin(2.0 * Math.PI * frequencyHz * i / sampleRate);
        return samples;
    }
}
