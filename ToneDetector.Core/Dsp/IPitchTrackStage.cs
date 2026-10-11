namespace ToneDetector.Core.Dsp;

/// <summary>
/// A stage that smooths the per-frame pitch track produced by the detector. Operates in
/// absolute cents (100 * MIDI note number, i.e. log-frequency space) rather than linear Hz,
/// so smoothing is musically uniform across the whole pitch range instead of over-smoothing
/// high notes and under-smoothing low ones. Callers should invoke <see cref="Reset"/> at
/// note/silence boundaries so state doesn't drag across a discontinuity.
/// </summary>
public interface IPitchTrackStage
{
    bool Enabled { get; set; }

    double Process(double pitchCents);

    void Reset();
}
