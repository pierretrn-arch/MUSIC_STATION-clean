using System.Globalization;

namespace ToneDetector.Core.Transcript;

/// <summary>The track colours (Design/tokens.json track-1 ... track-8), assigned in order and
/// repeating after eight. No blue: blue means selection and the mic note.</summary>
public static class TrackPalette
{
    public static IReadOnlyList<string> Colours { get; } = new[]
    {
        "#F2A541", // track-1 amber (same as accent)
        "#E8739A", // track-2 rose
        "#A98BF5", // track-3 violet
        "#5CC98A", // track-4 green
        "#D6B48C", // track-5 sand
        "#3FC1B0", // track-6 teal
        "#F0875A", // track-7 coral
        "#B5D45A", // track-8 lime
    };

    public static IReadOnlyList<string> Names { get; } = new[] { "Amber", "Rose", "Violet", "Green", "Sand", "Teal", "Coral", "Lime" };

    /// <summary>Colour index for the n-th track (0-based): 0-7, then repeating.</summary>
    public static int IndexFor(int trackNumber) => ((trackNumber % Colours.Count) + Colours.Count) % Colours.Count;
}

/// <summary>Volume and pan scales for the tracks panel.</summary>
public static class MixerMath
{
    private const string MinusSign = "−";

    /// <summary>Fader position (0-1) where the level is 0 dB.</summary>
    public const double UnityPosition = 0.8;

    /// <summary>Level at the top of the fader.</summary>
    public const double MaxDb = 6.0;

    /// <summary>Fader position 0-1 to dB: 0 = silent (negative infinity), 0.8 = 0 dB, 1 = +6 dB.
    /// Below 0 dB the curve is 40·log10(position / 0.8), so the fader has more room near unity.</summary>
    public static double PositionToDb(double position)
    {
        position = Math.Clamp(position, 0.0, 1.0);
        if (position <= 0.0)
            return double.NegativeInfinity;
        if (position >= UnityPosition)
            return (position - UnityPosition) / (1.0 - UnityPosition) * MaxDb;
        return 40.0 * Math.Log10(position / UnityPosition);
    }

    public static double DbToPosition(double db)
    {
        if (double.IsNegativeInfinity(db))
            return 0.0;
        if (db >= 0.0)
            return Math.Min(1.0, UnityPosition + db / MaxDb * (1.0 - UnityPosition));
        return UnityPosition * Math.Pow(10.0, db / 40.0);
    }

    /// <summary>Linear gain for a fader position (0 when silent).</summary>
    public static double PositionToGain(double position)
    {
        double db = PositionToDb(position);
        return double.IsNegativeInfinity(db) ? 0.0 : Math.Pow(10.0, db / 20.0);
    }

    /// <summary>"0.0", "−4.5", "+6.0", or "−∞" when silent (the unit is shown separately).</summary>
    public static string DbText(double position)
    {
        double db = PositionToDb(position);
        if (double.IsNegativeInfinity(db))
            return MinusSign + "∞";
        double rounded = Math.Round(db, 1, MidpointRounding.AwayFromZero);
        string magnitude = Math.Abs(rounded).ToString("0.0", CultureInfo.InvariantCulture);
        return rounded > 0 ? "+" + magnitude : rounded < 0 ? MinusSign + magnitude : magnitude;
    }

    /// <summary>Pan -100 (left) to +100 (right) as "C", "L20" or "R15".</summary>
    public static string PanText(int pan)
    {
        pan = Math.Clamp(pan, -100, 100);
        return pan == 0 ? "C" : pan < 0 ? $"L{-pan}" : $"R{pan}";
    }

    public static int ClampPan(int pan) => Math.Clamp(pan, -100, 100);

    /// <summary>Whether a track plays: not muted, and soloed if any track is soloed.</summary>
    public static bool IsAudible(bool muted, bool solo, bool anySolo) => !muted && (!anySolo || solo);

    /// <summary>MIDI volume (CC 7): 127 at 0 dB and above, scaled down with the fader below it.</summary>
    public static int MidiVolume(double position) =>
        (int)Math.Round(127.0 * Math.Clamp(position / UnityPosition, 0.0, 1.0));

    /// <summary>MIDI pan (CC 10): 0 left, 64 centre, 127 right.</summary>
    public static int MidiPan(int pan)
    {
        pan = ClampPan(pan);
        return (int)Math.Round(pan < 0 ? 64 + pan * 64 / 100.0 : 64 + pan * 63 / 100.0);
    }
}

/// <summary>A track as the exporters see it.</summary>
public sealed record TrackExport(
    string Name,
    string Colour,
    string Instrument,
    bool IsDrums,
    bool IsMuted,
    bool IsSolo,
    double VolumePosition,
    int Pan,
    IReadOnlyList<PianoRollNote> Notes);
