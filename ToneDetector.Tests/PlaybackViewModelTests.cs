using ToneDetector.App.ViewModels;
using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class PlaybackViewModelTests
{
    [Fact]
    public void Constructor_InitializesAndPlaysTheOutputDevice()
    {
        var fakePlayer = new FakeWavePlayer();
        using var viewModel = new PlaybackViewModel(() => new FakeSynth(), () => fakePlayer);

        Assert.Equal(NAudio.Wave.PlaybackState.Playing, fakePlayer.PlaybackState);
        Assert.NotNull(fakePlayer.OutputWaveFormat);
    }

    [Fact]
    public void AttachInputSource_ForwardsNoteEventsToTheSynthAndStartsTheSource()
    {
        var fakeSynth = new FakeSynth();
        using var viewModel = new PlaybackViewModel(() => fakeSynth, () => new FakeWavePlayer());
        var fakeSource = new FakeNoteInputSource();

        viewModel.AttachInputSource(fakeSource);
        fakeSource.RaiseNoteOn(60, 100);
        fakeSource.RaiseNoteOff(60);

        Assert.True(fakeSource.WasStarted);
        Assert.Contains((60, 100), fakeSynth.NoteOnCalls);
        Assert.Contains(60, fakeSynth.NoteOffCalls);
    }

    [Fact]
    public void DetachInputSource_StopsForwardingAndStopsTheSource()
    {
        var fakeSynth = new FakeSynth();
        using var viewModel = new PlaybackViewModel(() => fakeSynth, () => new FakeWavePlayer());
        var fakeSource = new FakeNoteInputSource();
        viewModel.AttachInputSource(fakeSource);

        viewModel.DetachInputSource(fakeSource);
        fakeSource.RaiseNoteOn(60, 100);

        Assert.True(fakeSource.WasStopped);
        Assert.Empty(fakeSynth.NoteOnCalls);
    }

    [Fact]
    public void SetSynthVoiceCount_ReplacesTheSynthAndRewiresAttachedSources()
    {
        var fakeSynth = new FakeSynth();
        using var viewModel = new PlaybackViewModel(() => fakeSynth, () => new FakeWavePlayer());
        var fakeSource = new FakeNoteInputSource();
        viewModel.AttachInputSource(fakeSource);

        viewModel.SetSynthVoiceCount(4);

        Assert.NotSame(fakeSynth, viewModel.SynthForTest);
        Assert.IsType<ToneDetector.Audio.Synth.SineSynthesizer>(viewModel.SynthForTest);

        // The fake synth should no longer receive events; the new real synth should.
        fakeSource.RaiseNoteOn(60, 100);
        Assert.Empty(fakeSynth.NoteOnCalls);
    }

    [Fact]
    public void SwappingSynth_DisposesThePreviousSynth_IfItIsDisposable()
    {
        var disposableSynth = new FakeDisposableSynth();
        using var viewModel = new PlaybackViewModel(() => disposableSynth, () => new FakeWavePlayer());

        // SetSynthVoiceCount is just a thin wrapper around the generic swap - exercising it here
        // proves the swap's disposal step works regardless of which concrete ISynth started active,
        // matching PHASE4-VST3-NATIVE.md's testing note that this should work "regardless of which
        // concrete ISynth is involved" without needing a real VstHostSynth/native plugin.
        viewModel.SetSynthVoiceCount(4);

        Assert.True(disposableSynth.WasDisposed);
    }

    [Fact]
    public void SwappingSynth_DoesNotThrow_WhenPreviousSynthIsNotDisposable()
    {
        var fakeSynth = new FakeSynth(); // not IDisposable
        using var viewModel = new PlaybackViewModel(() => fakeSynth, () => new FakeWavePlayer());

        viewModel.SetSynthVoiceCount(4); // should complete without an InvalidCastException or similar
    }

    [Fact]
    public void CurrentSynth_ReflectsTheActiveSynthAndRaisesPropertyChangedOnSwap()
    {
        var fakeSynth = new FakeSynth();
        using var viewModel = new PlaybackViewModel(() => fakeSynth, () => new FakeWavePlayer());
        Assert.Same(fakeSynth, viewModel.CurrentSynth);

        var raisedProperties = new List<string?>();
        viewModel.PropertyChanged += (_, e) => raisedProperties.Add(e.PropertyName);

        viewModel.SetSynthVoiceCount(4);

        Assert.Contains(nameof(PlaybackViewModel.CurrentSynth), raisedProperties);
        Assert.Same(viewModel.SynthForTest, viewModel.CurrentSynth);
        Assert.NotSame(fakeSynth, viewModel.CurrentSynth);
    }

    [Fact]
    public void Dispose_DisposesTheCurrentSynth_IfItIsDisposable()
    {
        var disposableSynth = new FakeDisposableSynth();
        var viewModel = new PlaybackViewModel(() => disposableSynth, () => new FakeWavePlayer());

        viewModel.Dispose();

        Assert.True(disposableSynth.WasDisposed);
    }

    // ---- Beat-based transport (runClock: false - the test moves the clock) ----

    private static PlaybackViewModel CreateEngine(FakeSynth synth) =>
        new(() => synth, () => new FakeWavePlayer(), runClock: false);

    private static PianoRollViewModel RollWith(PlaybackViewModel engine, params (int Midi, double Start, double Length)[] notes)
    {
        var roll = new PianoRollViewModel(engine);
        roll.ReplaceAll(notes.Select(n => new PianoRollNote(Guid.NewGuid(), n.Midi, n.Start, n.Length, Velocity: 90)));
        return roll;
    }

    [Fact]
    public void Play_SendsNotesAsTheBeatsPass() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1), (62, 1, 1), (64, 2, 1));

        engine.Play(roll);
        Assert.True(engine.IsPlaying);
        Assert.True(roll.IsPlaying);
        Assert.Same(roll, engine.ActiveRoll);
        Assert.Equal(new[] { (60, 90) }, synth.NoteOnCalls);

        engine.AdvanceForTest(1.0);
        Assert.Equal(new[] { 60, 62 }, synth.NoteOnCalls.Select(c => c.Note));
        Assert.Contains(60, synth.NoteOffCalls);
        Assert.Equal(1.0, roll.PositionBeats, 6);
    });

    [Fact]
    public void Play_StartsFromThePlayhead() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1), (67, 4, 1));
        roll.PositionBeats = 3;

        engine.Play(roll);
        Assert.Empty(synth.NoteOnCalls);
        engine.AdvanceForTest(1.0);

        Assert.Equal(67, Assert.Single(synth.NoteOnCalls).Note);
    });

    [Fact]
    public void Play_FromPastTheEnd_StartsAgainFromZero() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1));
        roll.PositionBeats = 10;

        engine.Play(roll);

        Assert.Equal(0.0, roll.PositionBeats);
        Assert.Equal(60, Assert.Single(synth.NoteOnCalls).Note);
    });

    [Fact]
    public void StopPlayback_ReleasesNotes_AndLeavesThePlayheadWhereItWas() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 4));
        engine.Play(roll);
        engine.AdvanceForTest(1.5);

        engine.StopPlayback();

        Assert.False(engine.IsPlaying);
        Assert.False(roll.IsPlaying);
        Assert.Null(engine.ActiveRoll);
        Assert.Contains(60, synth.NoteOffCalls);
        Assert.True(synth.AllNotesOffCallCount > 0);
        Assert.Equal(1.5, roll.PositionBeats, 6);
    });

    [Fact]
    public void ReachingTheEnd_StopsOnItsOwn() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1));
        engine.Play(roll);

        engine.AdvanceForTest(1.5);

        Assert.False(engine.IsPlaying);
        Assert.False(roll.IsPlaying);
    });

    [Fact]
    public void Loop_RepeatsTheRegion() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1), (62, 1, 1), (64, 5, 1));
        roll.SetLoop(0, 2);
        roll.IsLoopEnabled = true;
        engine.Play(roll);

        engine.AdvanceForTest(2.5);

        Assert.True(engine.IsPlaying);
        Assert.Equal(0.5, roll.PositionBeats, 6);
        Assert.Equal(new[] { 60, 62, 60 }, synth.NoteOnCalls.Select(c => c.Note));
        Assert.DoesNotContain(synth.NoteOnCalls, c => c.Note == 64);
    });

    [Fact]
    public void TurningLoopOnWhilePlaying_TakesEffectAtTheLoopEnd() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1), (64, 5, 1));
        roll.SetLoop(0, 2);
        engine.Play(roll);

        roll.IsLoopEnabled = true;
        engine.AdvanceForTest(2.5);

        Assert.Equal(0.5, roll.PositionBeats, 6);
    });

    [Fact]
    public void PlayingOneTab_StopsTheOther() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel live = RollWith(engine, (60, 0, 4));
        PianoRollViewModel file = RollWith(engine, (72, 0, 4));
        engine.Play(live);

        engine.Play(file);

        Assert.False(live.IsPlaying);
        Assert.True(file.IsPlaying);
        Assert.Contains(60, synth.NoteOffCalls);
        Assert.Equal(72, synth.NoteOnCalls[^1].Note);
    });

    [Fact]
    public void DeletingASoundingNote_ReleasesIt() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 4));
        engine.Play(roll);

        roll.DeleteNotes(roll.RollNotes.Select(n => n.Id).ToList());

        Assert.Contains(60, synth.NoteOffCalls);
    });

    [Fact]
    public void SoundingNotesAndKeys_FollowPlaybackAndHeldKeys() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1), (64, 1, 1));
        var keyboard = new FakeNoteInputSource();
        engine.AttachInputSource(keyboard);

        engine.Play(roll);
        Assert.Equal(new[] { roll.RollNotes[0].Id }, roll.SoundingNoteIds);
        Assert.Contains(60, roll.SoundingKeys);

        engine.AdvanceForTest(1.0);
        Assert.Equal(new[] { roll.RollNotes[1].Id }, roll.SoundingNoteIds);
        Assert.DoesNotContain(60, roll.SoundingKeys);

        keyboard.RaiseNoteOn(72, 100);
        Assert.Contains(72, roll.SoundingKeys);
        keyboard.RaiseNoteOff(72);
        Assert.DoesNotContain(72, roll.SoundingKeys);
    });

    [Fact]
    public void AnotherTabsPlayback_DoesNotLightThisTabsKeys() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel live = RollWith(engine, (60, 0, 1));
        PianoRollViewModel file = RollWith(engine, (72, 0, 1));

        engine.Play(file);

        Assert.Empty(live.SoundingKeys);
        Assert.Empty(live.SoundingNoteIds);
        Assert.Contains(72, file.SoundingKeys);
    });

    [Fact]
    public void RollTransport_PlayPauseStop() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 8));
        roll.SetLoop(4, 8);
        roll.IsLoopEnabled = true;

        roll.PlayPauseCommand.Execute(null);
        Assert.True(roll.IsPlaying);
        engine.AdvanceForTest(5);
        roll.PlayPauseCommand.Execute(null); // pause: the playhead stays
        Assert.False(roll.IsPlaying);
        Assert.Equal(5.0, roll.PositionBeats, 6);

        roll.StopCommand.Execute(null);
        Assert.Equal(4.0, roll.PositionBeats);
        roll.StopCommand.Execute(null);
        Assert.Equal(0.0, roll.PositionBeats);
    });

    [Fact]
    public void SeekTo_WhilePlaying_KeepsPlayingFromThere() => StaTestRunner.Run(() =>
    {
        var synth = new FakeSynth();
        using PlaybackViewModel engine = CreateEngine(synth);
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1), (67, 6, 1));
        engine.Play(roll);

        roll.SeekTo(6);

        Assert.True(roll.IsPlaying);
        Assert.Equal(67, synth.NoteOnCalls[^1].Note);
    });

    [Fact]
    public void PlayPause_NeedsNotes() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        var roll = new PianoRollViewModel(engine);
        Assert.False(roll.PlayPauseCommand.CanExecute(null));
    });

    // ---- Metronome and the recording clock ----

    [Fact]
    public void Metronome_ClicksEachBeatDuringPlayback_HigherOnBeatOne() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = RollWith(engine, (60, 0, 8));
        roll.IsMetronomeOn = true;

        engine.Play(roll);
        Assert.Equal(1, engine.ClickCountForTest); // beat 0
        Assert.True(engine.LastClickWasAccentForTest);

        engine.AdvanceForTest(1);
        Assert.Equal(2, engine.ClickCountForTest);
        Assert.False(engine.LastClickWasAccentForTest);

        engine.AdvanceForTest(3); // to beat 4: bar 2
        Assert.True(engine.LastClickWasAccentForTest);
    });

    [Fact]
    public void Metronome_Off_NoClicks_AndCanBeTurnedOnWhilePlaying() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = RollWith(engine, (60, 0, 8));

        engine.Play(roll);
        engine.AdvanceForTest(1);
        Assert.Equal(0, engine.ClickCountForTest);

        roll.IsMetronomeOn = true;
        engine.AdvanceForTest(1);
        Assert.Equal(1, engine.ClickCountForTest);
    });

    [Fact]
    public void RecordingClock_CountsInABar_ThenStartsOnThePlayhead() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = RollWith(engine, (60, 0, 1));
        roll.IsMetronomeOn = true;
        roll.PositionBeats = 8;
        double? startedAt = null;

        engine.StartRecording(roll, 4, beat => startedAt = beat);
        Assert.True(engine.IsRecordingClock);
        Assert.Equal(1, engine.ClickCountForTest); // count-in beat 1 (bar 2's downbeat minus a bar = beat 4)
        Assert.True(engine.LastClickWasAccentForTest);

        for (int i = 0; i < 3; i++)
            engine.AdvanceForTest(1);
        Assert.Null(startedAt);
        Assert.Equal(4, engine.ClickCountForTest); // beats 4, 5, 6, 7: the count-in bar
        Assert.Equal(8.0, roll.PositionBeats); // the playhead waits during the count-in

        engine.AdvanceForTest(1);
        Assert.Equal(8.0, startedAt!.Value, 6);
        Assert.Equal(5, engine.ClickCountForTest); // and the ticks carry on while recording

        engine.AdvanceForTest(1.5);
        Assert.Equal(9.5, roll.PositionBeats, 6);
        Assert.Empty(engine.SoundingNoteIds); // recording plays no notes
    });

    [Fact]
    public void RecordingClock_WithoutCountIn_StartsStraightAway() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = RollWith(engine);
        roll.PositionBeats = 2;
        double? startedAt = null;

        engine.StartRecording(roll, 0, beat => startedAt = beat);

        Assert.Equal(2.0, startedAt);
        Assert.Equal(0, engine.ClickCountForTest); // metronome off
    });

    [Fact]
    public void StopPlayback_EndsTheRecordingClock_LeavingThePlayheadAtTheEnd() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = RollWith(engine);
        engine.StartRecording(roll, 0, _ => { });
        engine.AdvanceForTest(6);

        engine.StopPlayback();

        Assert.False(engine.IsRecordingClock);
        Assert.Equal(6.0, roll.PositionBeats, 6);
    });

    [Fact]
    public void CancelledCountIn_NeverStartsRecording() => StaTestRunner.Run(() =>
    {
        using PlaybackViewModel engine = CreateEngine(new FakeSynth());
        PianoRollViewModel roll = RollWith(engine);
        bool started = false;
        engine.StartRecording(roll, 4, _ => started = true);

        engine.StopPlayback();
        engine.AdvanceForTest(8);

        Assert.False(started);
    });
}
