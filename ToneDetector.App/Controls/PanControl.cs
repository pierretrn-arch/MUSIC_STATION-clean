using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Controls;

/// <summary>
/// A track's pan as text ("Pan C", "Pan L20", "Pan R15"; Design/FileTracks.dc.html). Drag left or
/// right on it to change it (one step per 2px), double-click to centre, and while it has keyboard
/// focus Left/Right move it 5 at a time (the arrows scroll the roll otherwise).
/// </summary>
public sealed class PanControl : FrameworkElement
{
    public const int KeyStep = 5;
    private const double PixelsPerStep = 2.0;

    private Point? _dragStart;
    private int _dragStartValue;

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(PanControl),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
            null, (_, v) => MixerMath.ClampPan((int)v)));

    /// <summary>-100 (left) to 100 (right).</summary>
    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(PanControl),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public PanControl()
    {
        Focusable = true;
        Cursor = Cursors.SizeWE;
        Height = 18;
        MinWidth = 56;
        AutomationProperties.SetName(this, "Pan");
        ToolTip = "Pan: drag left or right, double-click to centre, or use the arrow keys";
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    internal string Text => "Pan " + MixerMath.PanText(Value);

    protected override Size MeasureOverride(Size availableSize)
    {
        FormattedText text = Format();
        return new Size(Math.Max(MinWidth, text.Width + 4), Math.Max(Height, text.Height));
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // hit-testable everywhere
        FormattedText text = Format();
        dc.DrawText(text, new Point(0, (RenderSize.Height - text.Height) / 2));
        if (IsKeyboardFocused && TryFindResource("Pitch") is Brush ring)
            dc.DrawRoundedRectangle(null, new Pen(ring, 1.5), new Rect(-3, -1, text.Width + 6, RenderSize.Height + 2), 3, 3);
    }

    private FormattedText Format()
    {
        var family = TryFindResource("Mono") as FontFamily ?? new FontFamily("Consolas");
        return new FormattedText(Text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 11, Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (e.ClickCount == 2)
        {
            Value = 0;
            _dragStart = null;
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragStartValue = Value;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not Point start || !IsMouseCaptured)
            return;
        double dx = e.GetPosition(this).X - start.X;
        Value = MixerMath.ClampPan(_dragStartValue + (int)Math.Round(dx / PixelsPerStep));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragStart = null;
        ReleaseMouseCapture();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (StepFor(e.Key) is int step)
        {
            Value = MixerMath.ClampPan(Value + step);
            e.Handled = true;
        }
    }

    /// <summary>Left = 5 to the left, Right = 5 to the right.</summary>
    internal static int? StepFor(Key key) => key switch
    {
        Key.Left => -KeyStep,
        Key.Right => KeyStep,
        _ => null,
    };
}
