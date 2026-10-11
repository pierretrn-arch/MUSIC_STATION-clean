namespace ToneDetector.Core.Dsp;

/// <summary>
/// A stage that operates on raw audio samples (before pitch extraction), such as filtering
/// or resampling. Stages are stateful across calls so they can process a stream frame-by-frame
/// without discontinuities at frame boundaries.
/// </summary>
public interface IAudioStage
{
    bool Enabled { get; set; }

    /// <summary>Processes samples in place.</summary>
    void Process(Span<float> samples);

    /// <summary>Resets any internal filter state (e.g. when switching audio sources).</summary>
    void Reset();
}
