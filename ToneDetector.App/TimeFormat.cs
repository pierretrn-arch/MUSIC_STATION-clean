namespace ToneDetector.App;

/// <summary>Shared mm:ss.fff formatting for anything displaying a millisecond timestamp/duration -
/// the Grid's Start column and the transport's position/duration display both want the exact same
/// format, so this exists once rather than each maintaining its own copy.</summary>
public static class TimeFormat
{
    public static string MillisecondsToDisplay(double milliseconds) =>
        TimeSpan.FromMilliseconds(milliseconds).ToString(@"mm\:ss\.fff");
}
