using System.Windows;
using System.Windows.Media;
using ToneDetector.App.ViewModels;

namespace ToneDetector.App.Controls;

/// <summary>
/// Home pitch-history trace (Design/Main.dc.html): cents offset over the last
/// <see cref="PitchHistoryBuffer.WindowMs"/>, newest at the right edge, in a Bg well with a dashed
/// Pitch target line through the middle. ±<see cref="RangeCents"/> fills the height; larger offsets
/// clamp to the edge. Unvoiced samples break the line. Redraws whenever the buffer changes.
/// </summary>
public sealed class PitchHistoryChart : FrameworkElement
{
    public const double RangeCents = 50.0;
    private const double WellRadius = 6.0;
    private const double TraceThickness = 2.5;
    private const double TargetThickness = 1.5;

    public static readonly DependencyProperty HistoryProperty = DependencyProperty.Register(
        nameof(History), typeof(PitchHistoryBuffer), typeof(PitchHistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnHistoryChanged));

    public PitchHistoryChart()
    {
        // Subscribe only while in the tree, so a closed view doesn't keep redrawing.
        Loaded += (_, _) => Attach(History);
        Unloaded += (_, _) => Detach(History);
    }

    public PitchHistoryBuffer? History
    {
        get => (PitchHistoryBuffer?)GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    /// <summary>Maps a sample to chart coordinates: x from the right edge by age, y from the
    /// centre line by cents (up = sharp), both clamped inside the chart.</summary>
    internal static Point MapPoint(double timeMs, double cents, double latestMs, double windowMs, double width, double height)
    {
        double age = Math.Clamp(latestMs - timeMs, 0, windowMs);
        double x = width - age / windowMs * width;
        double halfHeight = height / 2;
        double usableHalf = Math.Max(0, halfHeight - TraceThickness / 2);
        double y = halfHeight - Math.Clamp(cents, -RangeCents, RangeCents) / RangeCents * usableHalf;
        return new Point(x, y);
    }

    private static void OnHistoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (PitchHistoryChart)d;
        if (!chart.IsLoaded)
            return;
        chart.Detach(e.OldValue as PitchHistoryBuffer);
        chart.Attach(e.NewValue as PitchHistoryBuffer);
    }

    private void Attach(PitchHistoryBuffer? buffer)
    {
        if (buffer is null)
            return;
        buffer.Changed -= OnBufferChanged;
        buffer.Changed += OnBufferChanged;
        InvalidateVisual();
    }

    private void Detach(PitchHistoryBuffer? buffer)
    {
        if (buffer is not null)
            buffer.Changed -= OnBufferChanged;
    }

    private void OnBufferChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        Brush well = FindThemeBrush("Bg", "#0F1012");
        Brush trace = FindThemeBrush("Accent", "#F2A541");
        var target = new SolidColorBrush(FindThemeColor("PitchColor", "#5FB3E8")) { Opacity = 0.45 };

        dc.DrawRoundedRectangle(well, null, new Rect(0, 0, width, height), WellRadius, WellRadius);

        // Dashes of 6 on, 6 off; DashStyle lengths are in multiples of the pen thickness.
        var targetPen = new Pen(target, TargetThickness)
        {
            DashStyle = new DashStyle(new[] { 6 / TargetThickness, 6 / TargetThickness }, 0),
        };
        dc.DrawLine(targetPen, new Point(0, height / 2), new Point(width, height / 2));

        if (History is not { LatestTimeMs: double latestMs } history)
            return;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext ctx = geometry.Open())
        {
            bool inSegment = false;
            foreach (PitchHistorySample sample in history.Samples)
            {
                if (sample.Cents is not double cents)
                {
                    inSegment = false;
                    continue;
                }
                Point p = MapPoint(sample.TimeMs, cents, latestMs, history.WindowMs, width, height);
                if (!inSegment)
                {
                    ctx.BeginFigure(p, isFilled: false, isClosed: false);
                    inSegment = true;
                }
                else
                {
                    ctx.LineTo(p, isStroked: true, isSmoothJoin: true);
                }
            }
        }
        geometry.Freeze();

        var tracePen = new Pen(trace, TraceThickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round,
        };
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), WellRadius, WellRadius));
        dc.DrawGeometry(null, tracePen, geometry);
        dc.Pop();
    }

    private Brush FindThemeBrush(string resourceKey, string fallbackHex) =>
        TryFindResource(resourceKey) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);

    private Color FindThemeColor(string resourceKey, string fallbackHex) =>
        TryFindResource(resourceKey) is Color color ? color : (Color)ColorConverter.ConvertFromString(fallbackHex)!;
}
