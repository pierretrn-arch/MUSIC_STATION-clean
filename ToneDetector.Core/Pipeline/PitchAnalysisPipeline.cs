using ToneDetector.Core.Dsp;
using ToneDetector.Core.Pitch;

namespace ToneDetector.Core.Pipeline;

/// <summary>
/// Orchestrates one analysis frame end-to-end: optional custom-sample-rate resampling,
/// raw-audio-domain filter stages, YIN pitch detection, then pitch-track-domain smoothing
/// stages (moving average / Kalman) applied in the cents domain. Shared by both the real-time
/// capture path and the offline/file batch path, so the two modes stay behaviorally identical.
/// </summary>
public sealed class PitchAnalysisPipeline
{
    private bool _wasVoiced;

    public IPitchDetector PitchDetector { get; set; } = new YinPitchDetector();

    /// <summary>Ordered raw-audio-domain stages (e.g. high-pass then low-pass), each independently toggleable.</summary>
    public List<IAudioStage> AudioStages { get; } = new();

    /// <summary>Ordered pitch-track-domain smoothing stages (e.g. moving average then/or Kalman).</summary>
    public List<IPitchTrackStage> PitchTrackStages { get; } = new();

    public Resampler Resampler { get; set; } = new();

    /// <summary>When set, incoming frames are resampled to this rate before analysis. Null = use the source's native rate.</summary>
    public int? TargetSampleRate { get; set; }

    public PitchAnalysisResult ProcessFrame(ReadOnlySpan<float> frame, int sampleRate)
    {
        float[] working = frame.ToArray();
        int effectiveSampleRate = sampleRate;

        if (TargetSampleRate is int targetRate && targetRate != sampleRate)
        {
            working = Resampler.Resample(working, sampleRate, targetRate);
            effectiveSampleRate = targetRate;
        }

        Span<float> span = working;
        foreach (IAudioStage stage in AudioStages)
            stage.Process(span);

        PitchFrameResult pitchResult = PitchDetector.DetectPitch(span, effectiveSampleRate);

        if (!pitchResult.IsVoiced)
        {
            if (_wasVoiced)
            {
                foreach (IPitchTrackStage stage in PitchTrackStages)
                    stage.Reset();
            }
            _wasVoiced = false;

            return new PitchAnalysisResult(
                pitchResult.F0Hz, SmoothedF0Hz: 0.0, MidiNote: 0, CentsDeviation: 0.0,
                IsVoiced: false, pitchResult.RmsEnergy, pitchResult.Aperiodicity);
        }

        _wasVoiced = true;
        double absoluteCents = NoteQuantizer.FrequencyToAbsoluteCents(pitchResult.F0Hz);
        foreach (IPitchTrackStage stage in PitchTrackStages)
            absoluteCents = stage.Process(absoluteCents);

        double smoothedF0Hz = NoteQuantizer.AbsoluteCentsToFrequency(absoluteCents);
        (int midiNote, double centsDeviation) = NoteQuantizer.QuantizeToNearestNote(absoluteCents);

        return new PitchAnalysisResult(
            pitchResult.F0Hz, smoothedF0Hz, midiNote, centsDeviation,
            IsVoiced: true, pitchResult.RmsEnergy, pitchResult.Aperiodicity);
    }

    public void Reset()
    {
        foreach (IAudioStage stage in AudioStages)
            stage.Reset();
        foreach (IPitchTrackStage stage in PitchTrackStages)
            stage.Reset();
        _wasVoiced = false;
    }
}
