namespace ToneDetector.Core.Pitch;

/// <summary>
/// Estimates the fundamental frequency of a single analysis frame of mono audio samples.
/// Implementations are expected to be stateless/reentrant per call (no cross-frame state),
/// so higher layers (real-time vs. offline) can share the same instance.
/// </summary>
public interface IPitchDetector
{
    PitchFrameResult DetectPitch(ReadOnlySpan<float> frame, int sampleRate);
}
