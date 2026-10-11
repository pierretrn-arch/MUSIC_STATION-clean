using ToneDetector.App.ViewModels;
using ToneDetector.Core.Pipeline;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Home pitch panel readouts on <see cref="LiveTunerViewModel"/>: note split, target
/// frequency, offset text and tuning state, and the pitch history. Uses an empty device manager so
/// the pipeline never auto-starts, then drives state through the internal test setters and
/// <see cref="LiveTunerViewModel.SimulateFrameForTest"/>.</summary>
public class LiveTunerViewModelPitchReadoutTests
{
    private static LiveTunerViewModel CreateListeningViewModel()
    {
        var viewModel = new LiveTunerViewModel(
            new PipelineSettingsViewModel(),
            new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()),
            () => new FakeAudioCapture(),
            new FakeAudioDeviceManager());
        viewModel.IsPipelineRunning = true;
        return viewModel;
    }

    // MIDI 69 = A4, 61 = C#4, 60 = C4
    private static PitchAnalysisResult Voiced(int midiNote, double cents, double f0Hz = 440.0) =>
        new(f0Hz, f0Hz, midiNote, cents, IsVoiced: true, RmsEnergy: 0.5, Aperiodicity: 0.05);

    private static PitchAnalysisResult Unvoiced() =>
        new(0, 0, 0, 0, IsVoiced: false, RmsEnergy: 0.0, Aperiodicity: 1.0);

    [Fact]
    public void NoPitch_ShowsDashesAndNoOctave()
    {
        var viewModel = CreateListeningViewModel();

        Assert.False(viewModel.HasPitch);
        Assert.Equal("—", viewModel.NoteLetter);
        Assert.Equal("", viewModel.NoteOctave);
        Assert.Equal("—", viewModel.DetectedFrequencyText);
        Assert.Equal("—", viewModel.TargetFrequencyText);
        Assert.Equal("—", viewModel.OffsetText);
        Assert.Equal("cents", viewModel.OffsetLabel);
        Assert.Equal(TuningState.None, viewModel.TuningState);
        Assert.Null(viewModel.DisplayCents);
    }

    [Fact]
    public void NotListening_HidesPitchEvenWithACurrentPitch()
    {
        var viewModel = CreateListeningViewModel();
        viewModel.CurrentPitch = new PitchDisplay("A", 4);
        viewModel.IsPipelineRunning = false;

        Assert.False(viewModel.HasPitch);
        Assert.Equal("—", viewModel.NoteLetter);
        Assert.Null(viewModel.TargetFrequencyHz);
    }

    [Fact]
    public void NoteLetterAndOctave_SplitTheNoteWithGlyphs()
    {
        var viewModel = CreateListeningViewModel();
        viewModel.CurrentPitch = new PitchDisplay("C#", 3);

        Assert.Equal("C#", viewModel.NoteLetter);
        Assert.Equal("3", viewModel.NoteOctave);
    }

    [Theory]
    [InlineData("A", 4, 440.0)]
    [InlineData("C", 4, 261.63)]
    [InlineData("A", 3, 220.0)]
    public void TargetFrequencyHz_IsTheEqualTemperedNoteFrequency(string note, int octave, double expectedHz)
    {
        var viewModel = CreateListeningViewModel();
        viewModel.CurrentPitch = new PitchDisplay(note, octave);

        Assert.Equal(expectedHz, viewModel.TargetFrequencyHz!.Value, precision: 2);
    }

    [Fact]
    public void Frame_FillsTheTilesWithTwoDecimalsAndAPoint()
    {
        var viewModel = CreateListeningViewModel();

        viewModel.SimulateFrameForTest(Voiced(69, 2.0, f0Hz: 440.51));

        Assert.Equal("A", viewModel.NoteLetter);
        Assert.Equal("4", viewModel.NoteOctave);
        Assert.Equal("440.51", viewModel.DetectedFrequencyText);
        Assert.Equal("440.00", viewModel.TargetFrequencyText);
        Assert.Equal("+2", viewModel.OffsetText);
        Assert.Equal("cents · in tune", viewModel.OffsetLabel);
        Assert.Equal(2.0, viewModel.DisplayCents);
    }

    [Fact]
    public void Frame_FlatAndSharpLabels()
    {
        var viewModel = CreateListeningViewModel();

        viewModel.SimulateFrameForTest(Voiced(69, -12.0));
        Assert.Equal(TuningState.Flat, viewModel.TuningState);
        Assert.Equal("−12", viewModel.OffsetText);
        Assert.Equal("cents · flat", viewModel.OffsetLabel);

        viewModel.SimulateFrameForTest(Voiced(69, 9.0));
        Assert.Equal(TuningState.Sharp, viewModel.TuningState);
        Assert.Equal("+9", viewModel.OffsetText);
        Assert.Equal("cents · sharp", viewModel.OffsetLabel);
    }

    [Theory]
    [InlineData(0.0, TuningState.InTune)]
    [InlineData(5.0, TuningState.InTune)]
    [InlineData(-5.0, TuningState.InTune)]
    [InlineData(5.4, TuningState.InTune)]  // displays as +5
    [InlineData(-5.4, TuningState.InTune)] // displays as −5
    [InlineData(5.5, TuningState.Sharp)]   // displays as +6
    [InlineData(-5.5, TuningState.Flat)]   // displays as −6
    [InlineData(49.0, TuningState.Sharp)]
    [InlineData(-49.0, TuningState.Flat)]
    public void ClassifyTuning_UsesTheDisplayedWholeCents(double cents, TuningState expected)
    {
        Assert.Equal(expected, LiveTunerViewModel.ClassifyTuning(cents));
    }

    [Theory]
    [InlineData(2.4, "+2")]
    [InlineData(2.5, "+3")]
    [InlineData(-3.2, "−3")]
    [InlineData(0.3, "0")]
    [InlineData(-0.4, "0")]
    [InlineData(-50.0, "−50")]
    public void FormatSignedCents_UsesARealMinusSign(double cents, string expected)
    {
        Assert.Equal(expected, LiveTunerViewModel.FormatSignedCents(cents));
    }

    [Fact]
    public void Frames_AppendToThePitchHistory_WithGapsForUnvoiced()
    {
        var viewModel = CreateListeningViewModel();

        viewModel.SimulateFrameForTest(Voiced(69, 3.0));
        viewModel.SimulateFrameForTest(Unvoiced());
        viewModel.SimulateFrameForTest(Voiced(69, -4.0));

        var cents = viewModel.PitchHistory.Samples.Select(s => s.Cents).ToArray();
        Assert.Equal(new double?[] { 3.0, null, -4.0 }, cents);
    }

    [Fact]
    public void PitchHistory_WindowIsTheNamedConstant()
    {
        var viewModel = CreateListeningViewModel();

        Assert.Equal(5.0, LiveTunerViewModel.PitchHistoryWindowSeconds);
        Assert.Equal(LiveTunerViewModel.PitchHistoryWindowSeconds * 1000.0, viewModel.PitchHistory.WindowMs);
    }

    [Fact]
    public void Header_WithNoDevice_SaysSoAndHasNoRate()
    {
        var viewModel = CreateListeningViewModel();

        Assert.False(viewModel.HasDevice);
        Assert.Equal("No input device", viewModel.DeviceName);
    }

    [Fact]
    public void SampleRateText_WhenNotListening_IsADashWithTheUnit()
    {
        var viewModel = CreateListeningViewModel();

        Assert.Null(viewModel.SampleRateHz);
        Assert.Equal("— kHz", viewModel.SampleRateText);
    }

    [Fact]
    public void Pill_InTune_ShowsTheNoteAndInTune()
    {
        var viewModel = CreateListeningViewModel();

        viewModel.SimulateFrameForTest(Voiced(61, 3.0)); // C#4

        Assert.Equal("C#4", viewModel.PitchNoteText);
        Assert.Equal("in tune", viewModel.PitchStatusDetail);
    }

    [Theory]
    [InlineData(-12.0, "−12 cents")]
    [InlineData(12.0, "+12 cents")]
    [InlineData(-5.5, "−6 cents")]
    public void Pill_OutOfTune_ShowsTheSignedOffset(double cents, string expected)
    {
        var viewModel = CreateListeningViewModel();

        viewModel.SimulateFrameForTest(Voiced(69, cents));

        Assert.Equal(expected, viewModel.PitchStatusDetail);
    }

    [Fact]
    public void Pill_NoPitch_HasNoNoteOrDetail()
    {
        var viewModel = CreateListeningViewModel();

        viewModel.SimulateFrameForTest(Unvoiced());

        Assert.Equal("", viewModel.PitchNoteText);
        Assert.Equal("", viewModel.PitchStatusDetail);
    }

    [Fact]
    public void NoPitchText_DependsOnListening()
    {
        var viewModel = CreateListeningViewModel();
        Assert.Equal("No pitch", viewModel.NoPitchText);

        viewModel.IsPipelineRunning = false;
        Assert.Equal("Not listening", viewModel.NoPitchText);
    }

    [Fact]
    public void NoPitchText_RaisesChanged_WhenListeningChanges()
    {
        var viewModel = CreateListeningViewModel();
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.IsPipelineRunning = false;

        Assert.Contains(nameof(LiveTunerViewModel.NoPitchText), raised);
        Assert.Contains(nameof(LiveTunerViewModel.HasPitch), raised);
    }

    [Fact]
    public void ReadoutProperties_RaiseChanged_WhenAFrameArrives()
    {
        var viewModel = CreateListeningViewModel();
        var raised = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        viewModel.SimulateFrameForTest(Voiced(69, 1.0));

        foreach (string name in new[]
                 {
                     nameof(LiveTunerViewModel.NoteLetter), nameof(LiveTunerViewModel.NoteOctave),
                     nameof(LiveTunerViewModel.DetectedFrequencyText), nameof(LiveTunerViewModel.TargetFrequencyText),
                     nameof(LiveTunerViewModel.OffsetText), nameof(LiveTunerViewModel.OffsetLabel),
                     nameof(LiveTunerViewModel.TuningState), nameof(LiveTunerViewModel.DisplayCents),
                     nameof(LiveTunerViewModel.PitchNoteText), nameof(LiveTunerViewModel.PitchStatusDetail),
                 })
        {
            Assert.Contains(name, raised);
        }
    }
}
