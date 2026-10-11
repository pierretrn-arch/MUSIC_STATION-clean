namespace ToneDetector.Core.Framing;

/// <summary>
/// Buffers a continuous stream of mono samples (from live capture or file decode) and emits
/// fixed-size, overlapping analysis frames as enough data accumulates. Shared by the real-time
/// and offline paths so both frame the signal identically before handing frames to the pipeline.
/// </summary>
public sealed class SlidingWindowFrameAccumulator
{
    private readonly List<float> _buffer = new();

    public int WindowSize { get; }
    public int HopSize { get; }

    public event Action<float[]>? FrameReady;

    public SlidingWindowFrameAccumulator(int windowSize, int hopSize)
    {
        if (windowSize < 1)
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        if (hopSize < 1)
            throw new ArgumentOutOfRangeException(nameof(hopSize));

        WindowSize = windowSize;
        HopSize = hopSize;
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        foreach (float sample in samples)
            _buffer.Add(sample);

        while (_buffer.Count >= WindowSize)
        {
            float[] frame = _buffer.GetRange(0, WindowSize).ToArray();
            FrameReady?.Invoke(frame);

            int removeCount = Math.Min(HopSize, _buffer.Count);
            _buffer.RemoveRange(0, removeCount);
        }
    }

    public void Reset() => _buffer.Clear();
}
