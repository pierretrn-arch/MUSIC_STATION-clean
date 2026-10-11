using ToneDetector.Core.Framing;
using Xunit;

namespace ToneDetector.Tests;

public class SlidingWindowFrameAccumulatorTests
{
    [Fact]
    public void Write_EmitsOverlappingFramesAtHopIntervals()
    {
        var accumulator = new SlidingWindowFrameAccumulator(windowSize: 4, hopSize: 2);
        var emittedFrames = new List<float[]>();
        accumulator.FrameReady += frame => emittedFrames.Add(frame);

        accumulator.Write(new float[] { 1, 2, 3, 4, 5, 6, 7, 8 });

        Assert.Equal(3, emittedFrames.Count);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, emittedFrames[0]);
        Assert.Equal(new float[] { 3, 4, 5, 6 }, emittedFrames[1]);
        Assert.Equal(new float[] { 5, 6, 7, 8 }, emittedFrames[2]);
    }

    [Fact]
    public void Write_WithInsufficientSamples_EmitsNoFrames()
    {
        var accumulator = new SlidingWindowFrameAccumulator(windowSize: 100, hopSize: 50);
        var emittedFrames = new List<float[]>();
        accumulator.FrameReady += frame => emittedFrames.Add(frame);

        accumulator.Write(new float[10]);

        Assert.Empty(emittedFrames);
    }

    [Fact]
    public void Reset_ClearsBufferedSamples()
    {
        var accumulator = new SlidingWindowFrameAccumulator(windowSize: 4, hopSize: 4);
        var emittedFrames = new List<float[]>();
        accumulator.FrameReady += frame => emittedFrames.Add(frame);

        accumulator.Write(new float[] { 1, 2 });
        accumulator.Reset();
        accumulator.Write(new float[] { 3, 4 });

        Assert.Empty(emittedFrames); // 2 + 2 samples never reach the window size of 4 across the reset
    }
}
