using Melanchall.DryWetMidi.Interaction;
using ToneDetector.App.ViewModels;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class LiveTunerViewModelTests
{
    private static LiveTunerViewModel CreateViewModel() =>
        new(new PipelineSettingsViewModel(), new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer()), () => new FakeAudioCapture());

    [Fact]
    public void DisplayNote_WhenPipelineNotRunning_IsNotListening()
    {
        var viewModel = CreateViewModel();
        viewModel.IsPipelineRunning = false;

        Assert.Equal("Not listening", viewModel.DisplayNote);
    }

    [Fact]
    public void DisplayNote_WhenPipelineRunningWithNoCurrentPitch_IsListening()
    {
        var viewModel = CreateViewModel();
        viewModel.IsPipelineRunning = true;

        Assert.Equal("Listening…", viewModel.DisplayNote);
    }

    [Fact]
    public void DisplayNote_WhenPipelineRunningWithCurrentPitch_ShowsGlyphFormattedNote()
    {
        var viewModel = CreateViewModel();
        viewModel.IsPipelineRunning = true;
        viewModel.CurrentPitch = new PitchDisplay("C#", 3);

        Assert.Equal("C#3", viewModel.DisplayNote);
    }

    [Fact]
    public void DisplayNote_RaisesPropertyChanged_WhenIsPipelineRunningChanges()
    {
        var viewModel = CreateViewModel();
        viewModel.IsPipelineRunning = false;
        var raisedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        viewModel.IsPipelineRunning = true;

        Assert.Contains(nameof(LiveTunerViewModel.DisplayNote), raisedProperties);
    }

    [Fact]
    public void DisplayNote_RaisesPropertyChanged_WhenCurrentPitchChanges()
    {
        var viewModel = CreateViewModel();
        viewModel.IsPipelineRunning = true;
        var raisedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        viewModel.CurrentPitch = new PitchDisplay("A", 4);

        Assert.Contains(nameof(LiveTunerViewModel.DisplayNote), raisedProperties);
    }

    [Fact]
    public void ConstructingWithADevice_AutoStartsThePipeline()
    {
        var viewModel = CreateViewModel();

        // Only meaningful on a machine with at least one capture device; otherwise there's
        // nothing to auto-select and the pipeline correctly stays off.
        if (viewModel.CaptureDevices.Count > 0)
            Assert.True(viewModel.IsPipelineRunning);
    }

    [Fact]
    public void SimulateFrame_WhileRecording_AppendsCommittedEventsToTranscript()
    {
        var viewModel = CreateViewModel();
        viewModel.IsRecording = true;

        FeedFrames(viewModel, MakeVoicedFrame(440.0), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10); // real silence, commits the note

        Assert.Single(viewModel.Transcript);
        Assert.Equal("Note", viewModel.Transcript[0].Type);
    }

    [Fact]
    public void SimulateFrame_WhileNotRecording_DoesNotAppendToTranscript()
    {
        var viewModel = CreateViewModel();
        viewModel.IsRecording = false;

        FeedFrames(viewModel, MakeVoicedFrame(440.0), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);

        Assert.Empty(viewModel.Transcript);
    }

    [Fact]
    public void StartingRecording_DoesNotRetroactivelyIncludePreStartPitchData()
    {
        var viewModel = CreateViewModel();
        viewModel.IsRecording = false;

        FeedFrames(viewModel, MakeVoicedFrame(440.0), 10); // sung before Start: must not leak into the recorded note

        viewModel.IsRecording = true;
        FeedFrames(viewModel, MakeVoicedFrame(220.0), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10); // commits the note

        TranscriptEntryViewModel note = Assert.Single(viewModel.Transcript);
        Assert.Equal("Note", note.Type);
        Assert.Equal("A", note.Note);
        Assert.Equal(3, note.Octave); // A3 (220Hz), not A4 (440Hz) — no pre-Start contamination
    }

    [Fact]
    public void CommittedEventsAndTranscriptGrid_StayInSync()
    {
        var viewModel = CreateViewModel();
        viewModel.IsRecording = true;

        FeedFrames(viewModel, MakeVoicedFrame(440.0), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        FeedFrames(viewModel, MakeVoicedFrame(220.0), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);

        Assert.Equal(viewModel.CommittedEventsForTest.Count, viewModel.Transcript.Count);

        int noteCount = viewModel.CommittedEventsForTest.Count(e => e.Type == TranscriptEventType.Note);
        Transcript transcript = new("test", viewModel.CommittedEventsForTest.ToArray());
        int midiNoteCount = MidiExporter.BuildMidiFile(transcript).GetNotes().Count();
        Assert.Equal(noteCount, midiNoteCount);
    }

    [Fact]
    public void HideSilenceRows_DefaultsToTrue_AndFiltersSilenceOutOfTranscriptView()
    {
        var viewModel = CreateViewModel();
        viewModel.IsRecording = true;
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20); // 400ms - real, reported Silence
        FeedFrames(viewModel, MakeVoicedFrame(329.63), 10);

        Assert.True(viewModel.HideSilenceRows);
        Assert.Contains(viewModel.Transcript, e => e.Type == "Silence"); // still in the real data
        Assert.DoesNotContain(viewModel.TranscriptView.Cast<TranscriptEntryViewModel>(), e => e.Type == "Silence");
    }

    [Fact]
    public void HideSilenceRows_SetToFalse_ShowsSilenceRowsInTranscriptView()
    {
        var viewModel = CreateViewModel();
        viewModel.IsRecording = true;
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);
        FeedFrames(viewModel, MakeVoicedFrame(329.63), 10);

        viewModel.HideSilenceRows = false;

        Assert.Contains(viewModel.TranscriptView.Cast<TranscriptEntryViewModel>(), e => e.Type == "Silence");
    }

    [Fact]
    public void TranscriptView_StillIncludesEveryNoteRegardlessOfHideSilenceRows()
    {
        var viewModel = CreateViewModel();
        viewModel.IsRecording = true;
        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 20);
        FeedFrames(viewModel, MakeVoicedFrame(329.63), 10);

        int noteCountInFullTranscript = viewModel.Transcript.Count(e => e.Type == "Note");
        int noteCountInView = viewModel.TranscriptView.Cast<TranscriptEntryViewModel>().Count(e => e.Type == "Note");

        Assert.Equal(noteCountInFullTranscript, noteCountInView);
    }

    // ---- Play/Export off the attached PianoRoll's RollNotes, edits and all
    // (PHASE5-PIANOROLL-POLYPHONY-FOLLOWUP.md, milestones 6-7) ----

    [Fact]
    public void ExportCommands_CanExecute_ReflectsAttachedPianoRollsRollNotes_NotCommittedEvents()
    {
        var viewModel = CreateViewModel();
        var pianoRoll = new PianoRollViewModel(new PlaybackViewModel(() => new FakeSynth(), () => new FakeWavePlayer(), runClock: false), viewModel);
        viewModel.PianoRoll = pianoRoll;
        viewModel.IsRecording = true;

        Assert.False(viewModel.ExportJsonCommand.CanExecute(null));
        Assert.False(viewModel.ExportCsvCommand.CanExecute(null));
        Assert.False(viewModel.ExportMidiCommand.CanExecute(null));
        Assert.False(pianoRoll.PlayPauseCommand.CanExecute(null));

        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10); // commits the note - mirrors live into RollNotes too

        Assert.True(viewModel.ExportJsonCommand.CanExecute(null));
        Assert.True(viewModel.ExportCsvCommand.CanExecute(null));
        Assert.True(viewModel.ExportMidiCommand.CanExecute(null));
        Assert.False(pianoRoll.PlayPauseCommand.CanExecute(null)); // no playback while recording

        viewModel.IsRecording = false;
        Assert.True(pianoRoll.PlayPauseCommand.CanExecute(null));
    }

    [Fact]
    public void ExportCommands_CanExecute_WithNoPianoRollAttached_IsAlwaysFalse()
    {
        // Without MainViewModel's post-construction attach (e.g. a bare test-constructed
        // LiveTunerViewModel), there's nothing to read - matches today's "nothing to export" case
        // rather than falling back to the transcript.
        var viewModel = CreateViewModel();
        viewModel.IsRecording = true;

        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10);
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);

        Assert.NotEmpty(viewModel.CommittedEventsForTest);
        Assert.False(viewModel.ExportJsonCommand.CanExecute(null));
        Assert.False(viewModel.ExportCsvCommand.CanExecute(null));
        Assert.False(viewModel.ExportMidiCommand.CanExecute(null));
    }

    [Fact]
    public void Play_PlaysRollNotesReflectingAGridEdit_NotTheOriginalRecording() => StaTestRunner.Run(() =>
    {
        var fakeSynth = new FakeSynth();
        var playback = new PlaybackViewModel(() => fakeSynth, () => new FakeWavePlayer(), runClock: false);
        var viewModel = new LiveTunerViewModel(new PipelineSettingsViewModel(), playback, () => new FakeAudioCapture());
        var pianoRoll = new PianoRollViewModel(playback, viewModel);
        viewModel.PianoRoll = pianoRoll;
        viewModel.IsRecording = true;

        FeedFrames(viewModel, MakeVoicedFrame(261.63), 10); // ~C4 (MIDI 60)
        FeedFrames(viewModel, MakeUnvoicedFrame(), 10);
        viewModel.IsRecording = false;

        TranscriptEntryViewModel entry = Assert.Single(viewModel.Transcript);
        viewModel.ChangeEntryPitch(entry, newMidiNumber: 72); // Grid-tab edit - full-resyncs the roll too

        Assert.True(pianoRoll.PlayPauseCommand.CanExecute(null));
        pianoRoll.PlayPauseCommand.Execute(null);

        (int Note, int Velocity) noteOn = Assert.Single(fakeSynth.NoteOnCalls);
        Assert.Equal(72, noteOn.Note); // the edit, not the original 60
    });

    private static void FeedFrames(LiveTunerViewModel viewModel, PitchAnalysisResult frame, int count)
    {
        for (int i = 0; i < count; i++)
            viewModel.SimulateFrameForTest(frame);
    }

    private static PitchAnalysisResult MakeVoicedFrame(double f0Hz) =>
        new(f0Hz, f0Hz, 0, 0, IsVoiced: true, RmsEnergy: 0.5, Aperiodicity: 0.05);

    private static PitchAnalysisResult MakeUnvoicedFrame() =>
        new(0, 0, 0, 0, IsVoiced: false, RmsEnergy: 0.0, Aperiodicity: 1.0);
}
