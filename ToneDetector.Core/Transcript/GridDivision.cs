namespace ToneDetector.Core.Transcript;

/// <summary>The piano roll's Grid setting: snap for the time position, moving, resizing and
/// splitting, the length of a drawn note, and the grid lines.</summary>
public enum GridDivision
{
    Bar,
    Half,
    Quarter,
    Eighth,
    Sixteenth,
    ThirtySecond,
    EighthTriplet,
    SixteenthTriplet,
    Off,
}

public static class GridDivisions
{
    public const GridDivision Default = GridDivision.Sixteenth;

    /// <summary>Length of a drawn note with the grid off: a 16th.</summary>
    public const double OffNoteLengthBeats = 0.25;

    /// <summary>In the order the Grid menu lists them.</summary>
    public static IReadOnlyList<GridDivision> All { get; } = Enum.GetValues<GridDivision>();

    public static string Label(GridDivision division) => division switch
    {
        GridDivision.Bar => "Bar",
        GridDivision.Half => "1/2",
        GridDivision.Quarter => "1/4 (beat)",
        GridDivision.Eighth => "1/8",
        GridDivision.Sixteenth => "1/16",
        GridDivision.ThirtySecond => "1/32",
        GridDivision.EighthTriplet => "1/8T",
        GridDivision.SixteenthTriplet => "1/16T",
        _ => "Off",
    };

    /// <summary>Grid step in beats, or null for Off.</summary>
    public static double? LengthBeats(GridDivision division) => division switch
    {
        GridDivision.Bar => MusicalTime.BeatsPerBar,
        GridDivision.Half => 2.0,
        GridDivision.Quarter => 1.0,
        GridDivision.Eighth => 0.5,
        GridDivision.Sixteenth => 0.25,
        GridDivision.ThirtySecond => 0.125,
        GridDivision.EighthTriplet => 1.0 / 3.0,
        GridDivision.SixteenthTriplet => 1.0 / 6.0,
        _ => null,
    };

    /// <summary>Length of a note drawn with a click.</summary>
    public static double NoteLengthBeats(GridDivision division) => LengthBeats(division) ?? OffNoteLengthBeats;

    /// <summary>Nearest grid line; unchanged with the grid off or when <paramref name="enabled"/>
    /// is false (Alt held).</summary>
    public static double Snap(double beats, GridDivision division, bool enabled = true) =>
        enabled && LengthBeats(division) is double step ? Math.Round(beats / step) * step : beats;

    /// <summary>Grid line at or before the position (for drawing a note from where you click).</summary>
    public static double SnapDown(double beats, GridDivision division, bool enabled = true) =>
        enabled && LengthBeats(division) is double step ? Math.Floor(beats / step + 1e-9) * step : beats;

    public static GridDivision Parse(string? name) =>
        Enum.TryParse(name, out GridDivision division) && Enum.IsDefined(division) ? division : Default;
}
