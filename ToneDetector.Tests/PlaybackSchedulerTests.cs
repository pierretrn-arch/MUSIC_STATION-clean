using ToneDetector.Core.Transcript;
using Xunit;

namespace ToneDetector.Tests;

public class PlaybackSchedulerTests
{
    private static PianoRollNote N(int midi, double start, double length) => new(Guid.NewGuid(), midi, start, length, Velocity: 90);

    private static string Describe(IEnumerable<NoteEvent> events) =>
        string.Join(" ", events.Select(e => (e.Kind == NoteEventKind.On ? "+" : "-") + e.MidiNumber));

    [Fact]
    public void PlaysNotesInOrderAsThePlayheadMoves()
    {
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { N(60, 0, 1), N(62, 1, 1), N(64, 2, 2) });

        Assert.Equal("+60", Describe(scheduler.Start(0)));
        Assert.Equal("", Describe(scheduler.AdvanceTo(0.5)));
        Assert.Equal("-60 +62", Describe(scheduler.AdvanceTo(1.0)));
        Assert.Equal("-62 +64", Describe(scheduler.AdvanceTo(2.5)));
        Assert.False(scheduler.IsFinished);
        Assert.Equal("-64", Describe(scheduler.AdvanceTo(4.0)));
        Assert.True(scheduler.IsFinished);
    }

    [Fact]
    public void Start_MidNote_TriggersWhatIsSoundingThere()
    {
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { N(60, 0, 4), N(64, 1, 1), N(67, 3, 1) });

        Assert.Equal("+60", Describe(scheduler.Start(2.0)));
        Assert.Equal("+67", Describe(scheduler.AdvanceTo(3.0)));
    }

    [Fact]
    public void SamePitchRetrigger_ReleasesBeforeTheNextStarts()
    {
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { N(60, 0, 1), N(60, 1, 1) });
        scheduler.Start(0);

        IReadOnlyList<NoteEvent> events = scheduler.AdvanceTo(1.5);

        Assert.Equal("-60 +60", Describe(events));
    }

    [Fact]
    public void Loop_WrapsFromTheEndBackToTheStart()
    {
        var scheduler = new PlaybackScheduler { Loop = (4.0, 8.0) };
        scheduler.SetNotes(new[] { N(60, 4, 1), N(62, 7, 2), N(65, 9, 1) });
        Assert.Equal("", Describe(scheduler.Start(3.0)));
        Assert.Equal("+60", Describe(scheduler.AdvanceTo(4.0)));
        Assert.Equal("-60 +62", Describe(scheduler.AdvanceTo(7.5)));

        // Past the loop end: 62 is cut at the end, then 60 starts again at the loop start.
        Assert.Equal("-62 +60", Describe(scheduler.AdvanceTo(8.5)));
        Assert.Equal(4.5, scheduler.PositionBeats, 9);
        Assert.False(scheduler.IsFinished);
    }

    [Fact]
    public void Loop_StartedAfterTheRegion_PlaysOn()
    {
        var scheduler = new PlaybackScheduler { Loop = (0.0, 4.0) };
        scheduler.SetNotes(new[] { N(60, 5, 1) });
        scheduler.Start(4.5);

        Assert.Equal("+60", Describe(scheduler.AdvanceTo(5.5)));
        Assert.Equal(5.5, scheduler.PositionBeats, 9);
    }

    [Fact]
    public void SetNotes_DuringPlayback_ReleasesDeletedNotes()
    {
        PianoRollNote held = N(60, 0, 4);
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { held });
        scheduler.Start(0);
        scheduler.AdvanceTo(1);

        Assert.Equal("-60", Describe(scheduler.SetNotes(Array.Empty<PianoRollNote>())));
        Assert.True(scheduler.IsFinished || scheduler.PositionBeats < scheduler.EndBeat);
    }

    [Fact]
    public void SetNotes_ShortenedBehindThePlayhead_ReleasesIt()
    {
        PianoRollNote held = N(60, 0, 4);
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { held });
        scheduler.Start(0);
        scheduler.AdvanceTo(2);

        Assert.Equal("-60", Describe(scheduler.SetNotes(new[] { held with { DurationBeats = 1 } })));
    }

    [Fact]
    public void ReleaseAll_StopsEverything()
    {
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { N(60, 0, 4), N(64, 0, 4) });
        scheduler.Start(0);

        Assert.Equal(2, scheduler.ReleaseAll().Count);
        Assert.Empty(scheduler.SoundingMidiNumbers);
    }

    [Fact]
    public void VelocityComesFromTheNote()
    {
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { N(60, 0, 1) });
        Assert.Equal(90, scheduler.Start(0).Single().Velocity);
    }
}

public class MetronomeBeatTests
{
    private static PianoRollNote N(double start, double length) => new(Guid.NewGuid(), 60, start, length);

    [Fact]
    public void StartOnABeat_ClicksThatBeat_ThenEveryBeatPassed()
    {
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { N(0, 8) });

        scheduler.Start(0);
        Assert.Equal(new[] { 0 }, scheduler.DrainCrossedBeats());

        scheduler.AdvanceTo(0.9);
        Assert.Empty(scheduler.DrainCrossedBeats());
        scheduler.AdvanceTo(3.2);
        Assert.Equal(new[] { 1, 2, 3 }, scheduler.DrainCrossedBeats());
        scheduler.AdvanceTo(4.0);
        Assert.Equal(new[] { 4 }, scheduler.DrainCrossedBeats());
    }

    [Fact]
    public void StartBetweenBeats_WaitsForTheNextOne()
    {
        var scheduler = new PlaybackScheduler();
        scheduler.SetNotes(new[] { N(0, 8) });

        scheduler.Start(1.5);
        Assert.Empty(scheduler.DrainCrossedBeats());
        scheduler.AdvanceTo(2.1);
        Assert.Equal(new[] { 2 }, scheduler.DrainCrossedBeats());
    }

    [Fact]
    public void LoopWrap_ClicksTheLoopStart_NotTheLoopEnd()
    {
        var scheduler = new PlaybackScheduler { Loop = (4.0, 8.0) };
        scheduler.SetNotes(new[] { N(0, 16) });
        scheduler.Start(6.5);
        scheduler.DrainCrossedBeats();

        scheduler.AdvanceTo(8.5);

        Assert.Equal(new[] { 7, 4 }, scheduler.DrainCrossedBeats());
        Assert.Equal(4.5, scheduler.PositionBeats, 9);
    }
}
