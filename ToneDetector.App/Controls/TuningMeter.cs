using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ToneDetector.App.Controls;

/// <summary>
/// Home tuning meter (Design/Main.dc.html): a ±50-cent scale in a Bg well, the in-tune band in
/// Pitch, tick marks, labels underneath and the Accent needle. Stretches to any width; the height
/// is fixed at <see cref="MeterHeight"/> + label row. The needle is hidden while
/// <see cref="Cents"/> is null (no pitch). Label font comes from inherited TextElement.FontFamily.
/// </summary>
public sealed class TuningMeter : FrameworkElement
{
    public const double RangeCents = 50.0;
    private const double MeterHeight = 56.0;
    private const double LabelGap = 6.0;
    private const double LabelRowHeight = 18.0;
    private const double LabelFontSize = 13.0;
    private const double WellTop = 8.0;
    private const double WellHeight = 40.0;
    private const double WellRadius = 6.0;
    private const double NeedleWidth = 8.0;
    private const double TickThickness = 2.0;

    // The scale starts 2% in from each edge (20 of 1000 in the mockup).
    private const double InsetFraction = 0.02;

    private static readonly double[] MajorTicks = { -50, -25, 25, 50 };
    private static readonly double[] MinorTicks = { -40, -30, -20, -10, 10, 20, 30, 40 };
    private static readonly double[] LabelledTicks = { -50, -25, 0, 25, 50 };

    public static readonly DependencyProperty CentsProperty = DependencyProperty.Register(
        nameof(Cents), typeof(double?), typeof(TuningMeter),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InTuneCentsProperty = DependencyProperty.Register(
        nameof(InTuneCents), typeof(double), typeof(TuningMeter),
        new FrameworkPropertyMetadata(5.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Offset from the nearest note, clamped to ±50 for drawing; null hides the needle.</summary>
    public double? Cents
    {
        get => (double?)GetValue(CentsProperty);
        set => SetValue(CentsProperty, value);
    }

    /// <summary>Half-width of the in-tune band.</summary>
    public double InTuneCents
    {
        get => (double)GetValue(InTuneCentsProperty);
        set => SetValue(InTuneCentsProperty, value);
    }

    /// <summary>Horizontal position of <paramref name="cents"/> on a meter <paramref name="width"/> wide.</summary>
    internal static double CentsToX(double cents, double width)
    {
        double inset = width * InsetFraction;
        double clamped = Math.Clamp(cents, -RangeCents, RangeCents);
        return inset + (clamped + RangeCents) / (2 * RangeCents) * (width - 2 * inset);
    }

    /// <summary>"−50", "−25", "0", "+25", "+50" with a real minus sign.</summary>
    internal static string FormatTickLabel(double cents) =>
        cents == 0 ? "0" : cents > 0 ? $"+{cents:0}" : $"−{-cents:0}";

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        return new Size(width, MeterHeight + LabelGap + LabelRowHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        if (width <= 0)
            return;

        Brush well = FindThemeBrush("Bg", "#0F1012");
        Brush tick = FindThemeBrush("BorderStrong", "#4A4E56");
        Brush centreTick = FindThemeBrush("InkSubtle", "#A3A6AD");
        Brush needle = FindThemeBrush("Accent", "#F2A541");
        Brush label = FindThemeBrush("InkSubtle", "#A3A6AD");
        var band = new SolidColorBrush(FindThemeColor("PitchColor", "#5FB3E8")) { Opacity = 0.16 };

        dc.DrawRoundedRectangle(well, null, new Rect(0, WellTop, width, WellHeight), WellRadius, WellRadius);

        double bandLeft = CentsToX(-InTuneCents, width);
        double bandRight = CentsToX(InTuneCents, width);
        dc.DrawRectangle(band, null, new Rect(bandLeft, WellTop, bandRight - bandLeft, WellHeight));

        var tickPen = new Pen(tick, TickThickness);
        foreach (double c in MajorTicks)
            DrawTick(dc, tickPen, CentsToX(c, width), 20, 36);
        foreach (double c in MinorTicks)
            DrawTick(dc, tickPen, CentsToX(c, width), 24, 32);
        DrawTick(dc, new Pen(centreTick, TickThickness), CentsToX(0, width), 14, 42);

        if (Cents is double cents)
        {
            double x = CentsToX(cents, width);
            dc.DrawRoundedRectangle(needle, null, new Rect(x - NeedleWidth / 2, 2, NeedleWidth, 52), NeedleWidth / 2, NeedleWidth / 2);
        }

        var typeface = new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double labelTop = MeterHeight + LabelGap;
        foreach (double c in LabelledTicks)
        {
            var text = new FormattedText(FormatTickLabel(c), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                typeface, LabelFontSize, label, pixelsPerDip);
            double left = Math.Clamp(CentsToX(c, width) - text.Width / 2, 0, Math.Max(0, width - text.Width));
            dc.DrawText(text, new Point(left, labelTop + (LabelRowHeight - text.Height) / 2));
        }
    }

    private static void DrawTick(DrawingContext dc, Pen pen, double x, double top, double bottom) =>
        dc.DrawLine(pen, new Point(x, top), new Point(x, bottom));

    private Brush FindThemeBrush(string resourceKey, string fallbackHex) =>
        TryFindResource(resourceKey) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);

    private Color FindThemeColor(string resourceKey, string fallbackHex) =>
        TryFindResource(resourceKey) is Color color ? color : (Color)ColorConverter.ConvertFromString(fallbackHex)!;
}
