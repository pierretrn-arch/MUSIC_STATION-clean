using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToneDetector.Audio.Input;

namespace ToneDetector.App.Controls;

/// <summary>
/// On-screen piano keyboard: mouse-down on a key plays it, dragging across keys while held
/// glissandos (NoteOff the old key, NoteOn the new one), and releasing/leaving the control stops
/// whatever's currently sounding. Implements <see cref="INoteInputSource"/> like the other two
/// input sources, even though "Start"/"Stop" have little to do beyond releasing any stuck note —
/// this keeps all three sources interchangeable from the router's point of view.
/// </summary>
public sealed class VirtualKeyboardControl : Canvas, INoteInputSource
{
    private const int DefaultVelocity = 100;
    private static readonly HashSet<int> WhitePitchClasses = new() { 0, 2, 4, 5, 7, 9, 11 };

    private int? _currentlyPressedNote;
    private bool _isMouseDown;

    public event Action<int, int>? NoteOn;
    public event Action<int>? NoteOff;

    public static readonly DependencyProperty MinNoteProperty = DependencyProperty.Register(
        nameof(MinNote), typeof(int), typeof(VirtualKeyboardControl),
        new FrameworkPropertyMetadata(48, FrameworkPropertyMetadataOptions.AffectsRender)); // C3

    public int MinNote
    {
        get => (int)GetValue(MinNoteProperty);
        set => SetValue(MinNoteProperty, value);
    }

    public static readonly DependencyProperty MaxNoteProperty = DependencyProperty.Register(
        nameof(MaxNote), typeof(int), typeof(VirtualKeyboardControl),
        new FrameworkPropertyMetadata(84, FrameworkPropertyMetadataOptions.AffectsRender)); // C6

    public int MaxNote
    {
        get => (int)GetValue(MaxNoteProperty);
        set => SetValue(MaxNoteProperty, value);
    }

    public VirtualKeyboardControl()
    {
        ClipToBounds = true;
        Background = Brushes.Transparent; // ensure the whole area is hit-testable, not just painted pixels
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Nothing to start — this source is driven entirely by mouse events on the control
    /// itself — but implemented for interface symmetry with the other two sources.</summary>
    public void Start() { }

    public void Stop() => SimulateMouseUp();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        SimulateMouseDown(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SimulateMouseMove(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        SimulateMouseUp();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SimulateMouseLeave();
    }

    // ---- Core logic, split out from the WPF event overrides above so it's directly testable
    // with plain Point values instead of needing real routed mouse events. ----

    internal void SimulateMouseDown(Point position)
    {
        _isMouseDown = true;
        SetPressedNote(HitTestNote(position));
    }

    internal void SimulateMouseMove(Point position)
    {
        if (_isMouseDown)
            SetPressedNote(HitTestNote(position)); // glissando: only changes anything if the note differs
    }

    internal void SimulateMouseUp()
    {
        _isMouseDown = false;
        SetPressedNote(null);
    }

    internal void SimulateMouseLeave()
    {
        // Per spec, leaving the control while held ends the note — the user has dragged off the
        // keyboard, not onto an adjacent key (which would be an ordinary in-bounds MouseMove).
        _isMouseDown = false;
        SetPressedNote(null);
    }

    private void SetPressedNote(int? note)
    {
        if (note == _currentlyPressedNote)
            return;

        if (_currentlyPressedNote is int oldNote)
            NoteOff?.Invoke(oldNote);

        _currentlyPressedNote = note;

        if (note is int newNote)
            NoteOn?.Invoke(newNote, DefaultVelocity);

        InvalidateVisual();
    }

    /// <summary>Every white-key MIDI note in range, and every black key's MIDI note paired with
    /// its horizontal center position, expressed in units of "white key widths" from the left
    /// edge (i.e. multiply by the actual white-key pixel width to get a real X coordinate).</summary>
    private (List<int> WhiteNotes, List<(int Note, double CenterInWhiteKeyWidths)> BlackKeys) BuildLayout()
    {
        var whiteNotes = new List<int>();
        var blackKeys = new List<(int, double)>();

        for (int note = MinNote; note <= MaxNote; note++)
        {
            int pitchClass = ((note % 12) + 12) % 12;
            if (WhitePitchClasses.Contains(pitchClass))
                whiteNotes.Add(note);
            else
                blackKeys.Add((note, whiteNotes.Count)); // sits at the boundary just after the white keys so far
        }

        return (whiteNotes, blackKeys);
    }

    internal int? HitTestNote(Point position)
    {
        (List<int> whiteNotes, List<(int Note, double CenterInWhiteKeyWidths)> blackKeys) = BuildLayout();
        if (whiteNotes.Count == 0 || ActualWidth <= 0)
            return null;

        double whiteKeyWidth = ActualWidth / whiteNotes.Count;
        double blackKeyHeight = ActualHeight * 0.6;
        double blackKeyWidth = whiteKeyWidth * 0.6;

        if (position.Y <= blackKeyHeight)
        {
            foreach ((int note, double centerRatio) in blackKeys)
            {
                double centerX = centerRatio * whiteKeyWidth;
                if (position.X >= centerX - blackKeyWidth / 2 && position.X <= centerX + blackKeyWidth / 2)
                    return note;
            }
        }

        int whiteIndex = (int)(position.X / whiteKeyWidth);
        if (whiteIndex >= 0 && whiteIndex < whiteNotes.Count)
            return whiteNotes[whiteIndex];

        return null;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        double width = Math.Max(0, ActualWidth);
        double height = Math.Max(0, ActualHeight);
        if (width <= 0 || height <= 0)
            return;

        Brush whiteKeyBrush = FindThemeBrush("TextPrimaryBrush", "#F5F7FA");
        Brush blackKeyBrush = FindThemeBrush("BackgroundBrush", "#0A0E17");
        Brush borderBrush = FindThemeBrush("BorderBrush", "#2A3244");
        Brush pressedBrush = FindThemeBrush("AccentBlueBrush", "#60A5FA");
        var borderPen = new Pen(borderBrush, 1);

        (List<int> whiteNotes, List<(int Note, double CenterInWhiteKeyWidths)> blackKeys) = BuildLayout();
        if (whiteNotes.Count == 0)
            return;

        double whiteKeyWidth = width / whiteNotes.Count;
        double blackKeyHeight = height * 0.6;
        double blackKeyWidth = whiteKeyWidth * 0.6;

        for (int i = 0; i < whiteNotes.Count; i++)
        {
            Brush fill = whiteNotes[i] == _currentlyPressedNote ? pressedBrush : whiteKeyBrush;
            dc.DrawRectangle(fill, borderPen, new Rect(i * whiteKeyWidth, 0, whiteKeyWidth, height));
        }

        foreach ((int note, double centerRatio) in blackKeys)
        {
            Brush fill = note == _currentlyPressedNote ? pressedBrush : blackKeyBrush;
            double centerX = centerRatio * whiteKeyWidth;
            dc.DrawRectangle(fill, borderPen, new Rect(centerX - blackKeyWidth / 2, 0, blackKeyWidth, blackKeyHeight));
        }
    }

    private Brush FindThemeBrush(string resourceKey, string fallbackHex)
    {
        if (TryFindResource(resourceKey) is Brush brush)
            return brush;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);
    }
}
