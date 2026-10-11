namespace ToneDetector.Core.Dsp;

/// <summary>
/// Simple sliding-window average over the last N pitch-track samples (in cents).
/// </summary>
public sealed class MovingAverageSmoother : IPitchTrackStage
{
    private double[] _window;
    private int _count;
    private int _writeIndex;
    private double _sum;

    public bool Enabled { get; set; } = true;

    public int WindowSize { get; private set; }

    public MovingAverageSmoother(int windowSize = 5)
    {
        if (windowSize < 1)
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be at least 1.");

        WindowSize = windowSize;
        _window = new double[windowSize];
    }

    public void SetWindowSize(int windowSize)
    {
        if (windowSize < 1)
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Window size must be at least 1.");

        WindowSize = windowSize;
        _window = new double[windowSize];
        _count = 0;
        _writeIndex = 0;
        _sum = 0.0;
    }

    public double Process(double pitchCents)
    {
        if (!Enabled)
            return pitchCents;

        if (_count < WindowSize)
        {
            _window[_writeIndex] = pitchCents;
            _sum += pitchCents;
            _count++;
        }
        else
        {
            _sum -= _window[_writeIndex];
            _window[_writeIndex] = pitchCents;
            _sum += pitchCents;
        }

        _writeIndex = (_writeIndex + 1) % WindowSize;
        return _sum / _count;
    }

    public void Reset()
    {
        _count = 0;
        _writeIndex = 0;
        _sum = 0.0;
        Array.Clear(_window);
    }
}
