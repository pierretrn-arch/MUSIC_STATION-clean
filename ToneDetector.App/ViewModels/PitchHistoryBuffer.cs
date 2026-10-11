namespace ToneDetector.App.ViewModels;

/// <summary>Where a pitch sits relative to its nearest note, for the Home offset tile.</summary>
public enum TuningState
{
    /// <summary>No voiced pitch (or not listening).</summary>
    None,
    Flat,
    InTune,
    Sharp,
}

/// <summary>One pitch-history point: pipeline time and cents offset from the nearest note, or
/// null Cents for an unvoiced frame (drawn as a gap).</summary>
public readonly record struct PitchHistorySample(double TimeMs, double? Cents);

/// <summary>
/// Rolling window of recent cents offsets behind the Home pitch-history trace. Samples arrive in
/// time order; anything older than <see cref="WindowMs"/> before the newest sample is dropped.
/// Raises <see cref="Changed"/> after every change so the chart can redraw without rebinding.
/// </summary>
public sealed class PitchHistoryBuffer
{
    private readonly List<PitchHistorySample> _samples = new();

    public PitchHistoryBuffer(double windowMs)
    {
        if (windowMs <= 0)
            throw new ArgumentOutOfRangeException(nameof(windowMs), windowMs, "Window must be positive.");
        WindowMs = windowMs;
    }

    public double WindowMs { get; }

    /// <summary>Oldest first.</summary>
    public IReadOnlyList<PitchHistorySample> Samples => _samples;

    /// <summary>Time of the newest sample (the trace's right edge), or null when empty.</summary>
    public double? LatestTimeMs => _samples.Count > 0 ? _samples[^1].TimeMs : null;

    public event EventHandler? Changed;

    public void Add(double timeMs, double? cents)
    {
        _samples.Add(new PitchHistorySample(timeMs, cents));

        double cutoff = timeMs - WindowMs;
        int expired = 0;
        while (expired < _samples.Count && _samples[expired].TimeMs < cutoff)
            expired++;
        if (expired > 0)
            _samples.RemoveRange(0, expired);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (_samples.Count == 0)
            return;
        _samples.Clear();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
