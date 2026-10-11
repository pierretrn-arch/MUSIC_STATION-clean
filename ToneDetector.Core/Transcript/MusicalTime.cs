using System.Globalization;

namespace ToneDetector.Core.Transcript;

/// <summary>
/// Beats, bars and tempo for the piano roll. Notes are stored in beats (quarter notes) in 4/4;
/// milliseconds are always worked out from the tempo, so changing the tempo keeps every note on
/// its beat and only changes how fast it plays.
/// </summary>
public static class MusicalTime
{
    public const int BeatsPerBar = 4;
    public const int StepsPerBeat = 4; // a step is a 16th note
    public const string TimeSignature = "4/4";
    public const double MinBpm = 40;
    public const double MaxBpm = 240;
    public const double DefaultBpm = 120;

    /// <summary>Whole BPM within 40-240.</summary>
    public static double ClampBpm(double bpm) =>
        double.IsFinite(bpm) ? Math.Clamp(Math.Round(bpm), MinBpm, MaxBpm) : DefaultBpm;

    public static double MsPerBeat(double bpm) => 60000.0 / bpm;

    public static double MsToBeats(double ms, double bpm) => ms / MsPerBeat(bpm);

    public static double BeatsToMs(double beats, double bpm) => beats * MsPerBeat(bpm);

    /// <summary>1-based bar.beat.step, e.g. 0 beats -> "1.1.1", 9 beats -> "3.2.1".</summary>
    public static string FormatPosition(double beats)
    {
        long steps = (long)Math.Floor(Math.Max(0.0, beats) * StepsPerBeat + 1e-6);
        long bar = steps / (BeatsPerBar * StepsPerBeat) + 1;
        long beat = steps / StepsPerBeat % BeatsPerBar + 1;
        long step = steps % StepsPerBeat + 1;
        return string.Create(CultureInfo.InvariantCulture, $"{bar}.{beat}.{step}");
    }
}
