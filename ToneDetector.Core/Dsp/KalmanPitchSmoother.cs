namespace ToneDetector.Core.Dsp;

/// <summary>
/// 1D constant-velocity Kalman filter tracking pitch (in cents) and its rate of change.
/// Compared to a moving average, this gives smooth tracking with lower lag on glides/vibrato,
/// at the cost of two tuning parameters (process noise, measurement noise) that trade
/// responsiveness against smoothness.
/// </summary>
public sealed class KalmanPitchSmoother : IPitchTrackStage
{
    /// <summary>Named presets mapping to sane process/measurement noise combinations, since raw
    /// Q/R values are not meaningful to most users.</summary>
    public enum Preset
    {
        Fast,
        Smooth,
        Studio,
    }

    private double _position;
    private double _velocity;
    private double _pxx = 1.0;
    private double _pxv;
    private double _pvv = 1.0;
    private bool _initialized;

    public bool Enabled { get; set; } = true;

    /// <summary>Process noise on position (cents^2 per frame) — higher tracks faster pitch changes.</summary>
    public double ProcessNoisePosition { get; set; }

    /// <summary>Process noise on velocity (cents^2 per frame) — higher allows quicker glide/vibrato tracking.</summary>
    public double ProcessNoiseVelocity { get; set; }

    /// <summary>Measurement noise (cents^2) — higher trusts the raw YIN estimate less, smoothing more.</summary>
    public double MeasurementNoise { get; set; }

    public KalmanPitchSmoother(Preset preset = Preset.Smooth)
    {
        ApplyPreset(preset);
    }

    public void ApplyPreset(Preset preset)
    {
        (ProcessNoisePosition, ProcessNoiseVelocity, MeasurementNoise) = preset switch
        {
            Preset.Fast => (8.0, 4.0, 6.0),
            Preset.Smooth => (1.0, 0.5, 8.0),
            Preset.Studio => (0.2, 0.1, 10.0),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
    }

    public double Process(double pitchCents)
    {
        if (!Enabled)
            return pitchCents;

        if (!_initialized)
        {
            _position = pitchCents;
            _velocity = 0.0;
            _pxx = 1.0;
            _pxv = 0.0;
            _pvv = 1.0;
            _initialized = true;
            return _position;
        }

        // Predict (F = [[1,1],[0,1]], dt = 1 frame).
        double predictedPosition = _position + _velocity;
        double predictedVelocity = _velocity;
        double predictedPxx = _pxx + 2 * _pxv + _pvv + ProcessNoisePosition;
        double predictedPxv = _pxv + _pvv;
        double predictedPvv = _pvv + ProcessNoiseVelocity;

        // Update (H = [1, 0]).
        double innovation = pitchCents - predictedPosition;
        double innovationVariance = predictedPxx + MeasurementNoise;
        double kalmanGainPosition = predictedPxx / innovationVariance;
        double kalmanGainVelocity = predictedPxv / innovationVariance;

        _position = predictedPosition + kalmanGainPosition * innovation;
        _velocity = predictedVelocity + kalmanGainVelocity * innovation;
        _pxx = predictedPxx - kalmanGainPosition * predictedPxx;
        _pxv = predictedPxv - kalmanGainPosition * predictedPxv;
        _pvv = predictedPvv - kalmanGainVelocity * predictedPxv;

        return _position;
    }

    public void Reset()
    {
        _initialized = false;
        _position = 0.0;
        _velocity = 0.0;
        _pxx = 1.0;
        _pxv = 0.0;
        _pvv = 1.0;
    }
}
