using ToneDetector.Core.Analysis;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Pitch;

namespace ToneDetector.Core.Transcript;

/// <summary>
/// Causal, single-pass counterpart to <see cref="TranscriptBuilder"/>: consumes one pitch
/// analysis frame at a time (as produced live) and commits a <see cref="TranscriptEvent"/> as
/// soon as enough evidence has accumulated to finalize it, rather than requiring the whole
/// recording up front. Uses the same bridging/min-duration thresholds as the offline builder,
/// so feeding the same frame sequence through both produces an identical event sequence — the
/// only difference is *when* each event becomes available (immediately here vs. all at once
/// for the offline batch pass).
/// </summary>
public sealed class LiveTranscriptBuilder
{
    private double _minSilenceMs;
    private double _maxVoicedGapMs;

    private int _frameIndex;
    private bool? _confirmedVoiced;
    private double _segmentStartMs;
    private double _segmentEndMs;
    private double _centsSum;
    private double _confidenceSum;
    private readonly List<double> _centsDeviationSeries = new();
    private int _voicedFrameCount;
    private double? _pendingUnvoicedRunStartMs;

    /// <summary>Tentatively set to a candidate first-note's raw (pipeline-clock) start time as
    /// soon as that segment begins; every reported event's <c>StartTimeMs</c> is shifted by this
    /// amount, rebasing the session's time origin to the first note's actual onset
    /// (PHASE3-EXPANSION.md Change 8), since the pipeline's own clock runs continuously from well
    /// before Start is pressed. Stays tentative — and gets discarded along with it — if that
    /// segment never actually commits (see <see cref="_originConfirmed"/>); locked in permanently
    /// once a note built from it actually commits, so a later Stop/Start (same session, no Clear)
    /// doesn't re-arm leading-silence trimming a second time.</summary>
    private double? _sessionOriginMs;
    private bool _originConfirmed;

    public LiveTranscriptBuilder(double minSilenceMs = 150.0, double maxVoicedGapMs = 110.0)
    {
        _minSilenceMs = minSilenceMs;
        _maxVoicedGapMs = maxVoicedGapMs;
    }

    /// <summary>Settable (not just constructor-only) so a Settings-tab edit can apply immediately
    /// to a live session without discarding whatever segment is currently in progress - the
    /// in-flight accumulator state doesn't depend on these thresholds until the next frame is fed.</summary>
    public double MinSilenceMs
    {
        get => _minSilenceMs;
        set => _minSilenceMs = value;
    }

    public double MaxVoicedGapMs
    {
        get => _maxVoicedGapMs;
        set => _maxVoicedGapMs = value;
    }

    /// <summary>Feeds one analysis frame. Returns any transcript event(s) newly committed as a
    /// result (usually none, occasionally one).</summary>
    /// <summary>The pipeline clock: how much audio has been fed, in ms (before rebasing).</summary>
    public double ClockMs => _frameIndex * _frameDurationMs;

    /// <summary>The raw pipeline time reported events are shifted by (0 until the session's first
    /// note starts); an event's raw time is its <c>StartTimeMs</c> plus this.</summary>
    public double SessionOriginMs => _sessionOriginMs ?? 0.0;

    private double _frameDurationMs;

    public IReadOnlyList<TranscriptEvent> Feed(PitchAnalysisResult result, double frameDurationMs)
    {
        _frameDurationMs = frameDurationMs;
        double timestampMs = _frameIndex * frameDurationMs;
        _frameIndex++;
        double frameEndMs = timestampMs + frameDurationMs;

        if (_confirmedVoiced is null)
        {
            _confirmedVoiced = result.IsVoiced;
            _segmentStartMs = timestampMs;
            _segmentEndMs = frameEndMs;
            if (result.IsVoiced)
                _sessionOriginMs ??= timestampMs; // voice starts immediately: tentatively, this frame is the origin
            Accumulate(result);
            return Array.Empty<TranscriptEvent>();
        }

        if (_confirmedVoiced.Value)
            return FeedWhileBuildingNote(result, timestampMs, frameEndMs);

        return FeedWhileBuildingSilence(result, frameEndMs);
    }

    private IReadOnlyList<TranscriptEvent> FeedWhileBuildingNote(PitchAnalysisResult result, double timestampMs, double frameEndMs)
    {
        if (result.IsVoiced)
        {
            _pendingUnvoicedRunStartMs = null;
            _segmentEndMs = frameEndMs;
            Accumulate(result);
            return Array.Empty<TranscriptEvent>();
        }

        _pendingUnvoicedRunStartMs ??= timestampMs;
        _segmentEndMs = frameEndMs;

        double pendingDurationMs = frameEndMs - _pendingUnvoicedRunStartMs.Value;
        if (pendingDurationMs <= _maxVoicedGapMs)
            return Array.Empty<TranscriptEvent>();

        // The unvoiced run has outlasted the bridge tolerance: commit the note up to where the
        // run started, then begin a new (unconfirmed) silence segment from that same point.
        TranscriptEvent noteEvent = BuildNoteEvent(_pendingUnvoicedRunStartMs.Value);

        _confirmedVoiced = false;
        _segmentStartMs = _pendingUnvoicedRunStartMs.Value;
        _segmentEndMs = frameEndMs;
        _pendingUnvoicedRunStartMs = null;
        ResetAccumulators();

        return new[] { noteEvent };
    }

    private IReadOnlyList<TranscriptEvent> FeedWhileBuildingSilence(PitchAnalysisResult result, double frameEndMs)
    {
        if (!result.IsVoiced)
        {
            _segmentEndMs = frameEndMs;
            return Array.Empty<TranscriptEvent>();
        }

        // Any voiced frame ends the silence immediately (offline never bridges a voiced blip
        // sitting inside a silent stretch) — emit the silence only if it met the minimum duration
        // AND a note has already occurred this session (Change 8: this is either genuine
        // between-notes silence, reported as before, or it's the LEADING silence before the very
        // first note, which is never reported and doesn't even need to have met _minSilenceMs).
        var emitted = new List<TranscriptEvent>();
        double silenceDurationMs = _segmentEndMs - _segmentStartMs;
        bool isLeadingSilence = _sessionOriginMs is null;
        if (!isLeadingSilence && silenceDurationMs >= _minSilenceMs)
            emitted.Add(new TranscriptEvent(TranscriptEventType.Silence, Rebase(_segmentStartMs), silenceDurationMs, Note: null));

        _confirmedVoiced = true;
        _segmentStartMs = _segmentEndMs; // frame timestamps are contiguous; new segment starts where silence ended
        _segmentEndMs = frameEndMs;
        if (isLeadingSilence)
            _sessionOriginMs = _segmentStartMs; // this note is the first of the session: it becomes time zero
        ResetAccumulators();
        Accumulate(result);

        return emitted;
    }

    /// <summary>Call when the pipeline stops, to flush the in-progress segment as a final event.
    /// A trailing in-progress <b>note</b> is emitted (there's real evidence it happened); a
    /// trailing in-progress <b>silence</b> is always dropped rather than emitted — its start
    /// time reflects wherever this recording epoch began, not a continuation of whatever the UI
    /// was already displaying, so reporting it risks a bogus "gap since previous" once the
    /// caller's own timeline resumes elsewhere.</summary>
    public TranscriptEvent? Flush()
    {
        if (_confirmedVoiced is null)
            return null;

        TranscriptEvent? result = _confirmedVoiced.Value ? BuildNoteEvent(_segmentEndMs) : null;

        Reset();
        return result;
    }

    /// <summary>Discards whatever segment is currently in progress, without resetting the frame
    /// clock — so timestamps stay continuous (correct "gap since previous" math) while the next
    /// <see cref="Feed"/> call starts a brand-new segment rather than folding in frames that
    /// arrived before this call (e.g. audio captured while recording was paused). If no note has
    /// actually committed yet this session, also discards the tentative <see cref="_sessionOriginMs"/>
    /// along with it — otherwise a false start before Start was pressed could permanently lock in
    /// the wrong time-zero. Once a note HAS committed, pressing Start again (same accumulated
    /// grid/session, no Clear) deliberately leaves the now-confirmed origin alone: silence after
    /// resuming is ordinary between-notes silence, not a second "leading" silence to trim.</summary>
    public void DiscardInProgressSegment()
    {
        if (!_originConfirmed)
            _sessionOriginMs = null;
        _confirmedVoiced = null;
        _pendingUnvoicedRunStartMs = null;
        ResetAccumulators();
    }

    public void Reset()
    {
        _frameIndex = 0;
        _confirmedVoiced = null;
        _segmentStartMs = 0;
        _segmentEndMs = 0;
        _pendingUnvoicedRunStartMs = null;
        _sessionOriginMs = null;
        _originConfirmed = false;
        ResetAccumulators();
    }

    /// <summary>Shifts a raw pipeline-clock timestamp so the session's first note reports as
    /// <c>StartTimeMs == 0</c> (Change 8) — a no-op (identity) until that origin is established.</summary>
    private double Rebase(double rawMs) => rawMs - (_sessionOriginMs ?? 0.0);

    private void Accumulate(PitchAnalysisResult result)
    {
        if (!result.IsVoiced)
            return; // a tentatively-bridged gap frame: contributes duration only, not pitch data.

        _centsSum += NoteQuantizer.FrequencyToAbsoluteCents(result.SmoothedF0Hz);
        _confidenceSum += 1.0 - result.Aperiodicity;
        _voicedFrameCount++;
        _centsDeviationSeries.Add(result.CentsDeviation);
    }

    private void ResetAccumulators()
    {
        _centsSum = 0.0;
        _confidenceSum = 0.0;
        _voicedFrameCount = 0;
        _centsDeviationSeries.Clear();
    }

    private TranscriptEvent BuildNoteEvent(double endTimeMs)
    {
        _originConfirmed = true; // this note is real: lock in whatever origin it started from
        double durationMs = endTimeMs - _segmentStartMs;
        NoteInfo note;
        if (_voicedFrameCount == 0)
        {
            note = new NoteInfo(0, 0.0, 0.0, 0.0);
        }
        else
        {
            double meanCents = _centsSum / _voicedFrameCount;
            (int midiNote, double centsDeviation) = NoteQuantizer.QuantizeToNearestNote(meanCents);
            double meanF0Hz = NoteQuantizer.AbsoluteCentsToFrequency(meanCents);
            double meanConfidence = _confidenceSum / _voicedFrameCount;
            VibratoAnalysis? vibrato = VibratoAnalyzer.Analyze(_centsDeviationSeries, durationMs);
            note = new NoteInfo(midiNote, centsDeviation, meanF0Hz, meanConfidence, vibrato);
        }

        return new TranscriptEvent(TranscriptEventType.Note, Rebase(_segmentStartMs), durationMs, note);
    }
}
