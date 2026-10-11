using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ToneDetector.App.ViewModels;
using ToneDetector.Audio.Input;
using ToneDetector.Core.Pitch;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Controls;

/// <summary>
/// The piano roll shared by Live Tuner and File Analysis (Design/LiveTuner.dc.html,
/// FileAnalysis.dc.html, KeyColumn.dc.html). Works in beats: a ruler of bars and beats with
/// draggable S/E loop handles, grid lines that follow the tab's Grid setting, the key column, and
/// one <see cref="Rectangle"/> child per note of the selected track, in the track's colour (the
/// other tracks' notes are drawn behind as 22% ghosts and can't be edited). Rows are a fixed 24px
/// from A0 to C8 and scroll vertically. Edits go straight to <see cref="Editor"/> (the tab's
/// <see cref="PianoRollViewModel"/>), each as one Undo step:
/// <list type="bullet">
/// <item>Select: click a note to select it (Shift/Ctrl-click to add), drag its body to move the
/// selection, drag an edge to resize; drag on empty space for a marquee box.</item>
/// <item>Draw: click for a note one grid step long, or drag to set its length.</item>
/// <item>Erase: click or drag across notes to delete them.</item>
/// <item>Split: click a note to split it at that point.</item>
/// </list>
/// Moving, resizing, drawing, splitting, the loop handles and the time position snap to the grid;
/// holding Alt turns snapping off. A click on the ruler or on empty grid sets the time position.
/// Right-click opens Cut / Copy / Paste at [position] / Delete / Select all. Overlapping notes are
/// fine: the topmost is picked first and repeated clicks on the same spot reach the ones below.
/// While <see cref="IsLive"/> (recording) the view follows the moving playhead and editing is off.
/// Wheel scrolls in time, Ctrl+wheel scrolls up/down, Alt+wheel zooms time (remembered per tab);
/// the arrow keys scroll too (<see cref="ScrollByKey"/>).
/// Implements <see cref="INoteInputSource"/>: pressing a key plays it.
/// </summary>
public sealed class PianoRollControl : Canvas, INoteInputSource
{
    private double KeyboardWidth => KeyColumnWidth;
    private double RulerHeight => RulerRowHeight;

    // Key column spec (Design/KeyColumn.dc.html)
    private const double BlackKeyShortening = 26.0;
    private const double SelectedKeyBarWidth = 4.0;
    private const double PitchClassStripWidth = 6.0;
    private const double FullLabelMinRowHeight = 16.0;

    // Ruler spec (Design/LiveTuner.dc.html)
    private const double BeatTickHeight = 8.0;
    private const double LoopBandHeight = 16.0;
    private const double LoopHandleSize = 18.0;
    private const double MinBarLabelSpacingPx = 36.0;
    private const double MinGridLineSpacingPx = 6.0;

    private const double TimerIntervalMs = 50;

    /// <summary>Fixed row height (Design: 24px) and the key range, A0 to C8.</summary>
    public const double RowHeightPx = 24.0;
    public const int MinNote = 21;
    public const int MaxNote = 108;

    /// <summary>Ghost notes (other tracks) at 22% of their track colour.</summary>
    private const byte GhostAlpha = 0x38;
    private const double ResizeHandleWidthPx = 8.0;
    private const double ClickCycleTolerancePx = 2.0;
    private const double DragThresholdPx = 4.0;
    private const int DefaultVelocity = 100; // matches VirtualKeyboardControl's own default

    private static readonly Color[] PitchClassColors = BuildPitchClassColors();

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<Rectangle, PianoRollNote> _barToNote = new();
    private readonly Dictionary<Guid, Rectangle> _noteIdToBar = new();
    private readonly List<Guid> _selectedNoteIds = new();
    private readonly HashSet<Guid> _soundingNoteIds = new();
    private readonly HashSet<int> _soundingKeys = new();

    // Persists across rebuilds (keyed by the note's stable Id) so "most recently added/edited"
    // stays on top even though a rebuild re-creates every Rectangle.
    private readonly Dictionary<Guid, int> _zOrder = new();
    private int _zOrderCounter;

    // The note bars, needle and previews live in a layer clipped to the note area, so notes
    // scrolled up or left never draw over the ruler or key column.
    private readonly Canvas _noteLayer = new() { IsHitTestVisible = false };
    private System.Collections.ObjectModel.ObservableCollection<PianoRollNote>? _subscribedNotes;


    // "Repeated click in the same spot cycles to whatever's stacked underneath" state.
    private Point? _lastHitTestClickPos;
    private int _lastHitTestCandidateIndex;
    private Guid? _lastClickedNoteId;

    private enum DragMode { None, Move, ResizeLeft, ResizeRight, Draw, Marquee, Erase, Seek, LoopStart, LoopEnd, PendingClick }
    private DragMode _dragMode = DragMode.None;
    private Point _dragStart;
    private Guid? _dragNoteId;
    private PianoRollNote? _dragNote;
    private Dictionary<Guid, PianoRollNote> _dragOriginals = new();
    private double _dragDeltaBeats;
    private int _dragDeltaPitch;
    private double _dragPreviewStart;
    private double _dragPreviewDuration;
    private bool _dragMarqueeAdditive;
    private List<Guid> _marqueeBaseSelection = new();
    private readonly HashSet<Guid> _erasing = new();
    private int _drawMidi;
    private double _drawStartBeat;

    private readonly Rectangle _drawPreview = new() { RadiusX = 3, RadiusY = 3, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Rectangle _marquee = new()
    {
        RadiusX = 4,
        RadiusY = 4,
        StrokeThickness = 1.5,
        StrokeDashArray = new DoubleCollection { 4, 3 },
        IsHitTestVisible = false,
        Visibility = Visibility.Collapsed,
    };

    // Playhead: a real child (not OnRender) so it draws above the notes.
    private readonly Rectangle _needle = new() { Width = 2.0, Opacity = 0.85, IsHitTestVisible = false };
    private readonly System.Windows.Shapes.Path _needleMarker = new()
    {
        Data = Geometry.Parse("M 0 0 L 14 0 L 7 10 Z"),
        IsHitTestVisible = false,
    };

    // ---- Playable key column (INoteInputSource) ----
    private bool _isPlayingKeyboard;
    private int? _pressedKeyboardNote;
    private int? _hoverKeyboardNote;

    /// <inheritdoc/>
    public event Action<int, int>? NoteOn;
    /// <inheritdoc/>
    public event Action<int>? NoteOff;

    /// <summary>Raised whenever the selection changes (click, marquee, Select all, or <see cref="SetSelection"/>).</summary>
    public event EventHandler? SelectionChanged;

    public IReadOnlyList<Guid> SelectedNoteIds => _selectedNoteIds;

    // ---- Dependency properties ----

    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(
        nameof(Editor), typeof(PianoRollViewModel), typeof(PianoRollControl),
        new PropertyMetadata(null, OnEditorChanged));

    /// <summary>The tab's piano roll: notes, tempo, grid, tool, loop and playhead.</summary>
    public PianoRollViewModel? Editor
    {
        get => (PianoRollViewModel?)GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    // Drawn as a 6px strip on each key's outer edge (Design/KeyColumn.dc.html); notes stay Accent.
    public static readonly DependencyProperty ColorByPitchClassProperty = DependencyProperty.Register(
        nameof(ColorByPitchClass), typeof(bool), typeof(PianoRollControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool ColorByPitchClass
    {
        get => (bool)GetValue(ColorByPitchClassProperty);
        set => SetValue(ColorByPitchClassProperty, value);
    }

    public static readonly DependencyProperty KeyColumnWidthProperty = DependencyProperty.Register(
        nameof(KeyColumnWidth), typeof(double), typeof(PianoRollControl), new FrameworkPropertyMetadata(72.0, OnGeometryChanged));

    /// <summary>Width of the key column (72 in the design).</summary>
    public double KeyColumnWidth
    {
        get => (double)GetValue(KeyColumnWidthProperty);
        set => SetValue(KeyColumnWidthProperty, value);
    }

    public static readonly DependencyProperty RulerRowHeightProperty = DependencyProperty.Register(
        nameof(RulerRowHeight), typeof(double), typeof(PianoRollControl), new FrameworkPropertyMetadata(40.0, OnGeometryChanged));

    /// <summary>Height of the bar/beat ruler (40 in the design).</summary>
    public double RulerRowHeight
    {
        get => (double)GetValue(RulerRowHeightProperty);
        set => SetValue(RulerRowHeightProperty, value);
    }

    public static readonly DependencyProperty DetectedNoteProperty = DependencyProperty.Register(
        nameof(DetectedNote), typeof(int?), typeof(PianoRollControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>MIDI number of the live detected note: solid Pitch key and a tinted lane. Null for none.</summary>
    public int? DetectedNote
    {
        get => (int?)GetValue(DetectedNoteProperty);
        set => SetValue(DetectedNoteProperty, value);
    }

    public static readonly DependencyProperty IsLiveProperty = DependencyProperty.Register(
        nameof(IsLive), typeof(bool), typeof(PianoRollControl), new PropertyMetadata(false, OnIsLiveChanged));

    /// <summary>True while recording (bind to <c>IsRecording</c>): the view follows the moving
    /// playhead and editing is off.</summary>
    public bool IsLive
    {
        get => (bool)GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    public static readonly DependencyProperty ViewStartBeatsProperty = DependencyProperty.Register(
        nameof(ViewStartBeats), typeof(double), typeof(PianoRollControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewChanged, CoerceViewStart));

    /// <summary>Beat at the left edge of the grid. Bind a horizontal ScrollBar's Value to this
    /// (with <see cref="ScrollMaximumBeats"/> and <see cref="VisibleBeats"/>).</summary>
    public double ViewStartBeats
    {
        get => (double)GetValue(ViewStartBeatsProperty);
        set => SetValue(ViewStartBeatsProperty, value);
    }

    private static object CoerceViewStart(DependencyObject d, object value) => Math.Max(0.0, (double)value);

    public static readonly DependencyProperty ViewTopProperty = DependencyProperty.Register(
        nameof(ViewTop), typeof(double), typeof(PianoRollControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnViewChanged, CoerceViewTop));

    /// <summary>Vertical scroll in pixels from the top row (C8). Bind a vertical ScrollBar's
    /// Value to this (with <see cref="VerticalScrollMaximum"/> and <see cref="VisibleHeight"/>).</summary>
    public double ViewTop
    {
        get => (double)GetValue(ViewTopProperty);
        set => SetValue(ViewTopProperty, value);
    }

    private static object CoerceViewTop(DependencyObject d, object value)
    {
        var control = (PianoRollControl)d;
        double v = (double)value;
        return double.IsFinite(v) ? Math.Clamp(v, 0.0, control.MaxViewTop()) : 0.0;
    }

    private static readonly DependencyPropertyKey VisibleHeightPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(VisibleHeight), typeof(double), typeof(PianoRollControl), new PropertyMetadata(0.0));
    public static readonly DependencyProperty VisibleHeightProperty = VisibleHeightPropertyKey.DependencyProperty;

    /// <summary>Height of the note area (vertical ScrollBar ViewportSize).</summary>
    public double VisibleHeight => (double)GetValue(VisibleHeightProperty);

    private static readonly DependencyPropertyKey VerticalScrollMaximumPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(VerticalScrollMaximum), typeof(double), typeof(PianoRollControl), new PropertyMetadata(0.0));
    public static readonly DependencyProperty VerticalScrollMaximumProperty = VerticalScrollMaximumPropertyKey.DependencyProperty;

    /// <summary>Largest <see cref="ViewTop"/>: all 88 rows minus what fits.</summary>
    public double VerticalScrollMaximum => (double)GetValue(VerticalScrollMaximumProperty);

    private static readonly DependencyPropertyKey VisibleBeatsPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(VisibleBeats), typeof(double), typeof(PianoRollControl), new PropertyMetadata(20.0));
    public static readonly DependencyProperty VisibleBeatsProperty = VisibleBeatsPropertyKey.DependencyProperty;

    /// <summary>How many beats the grid shows (ScrollBar ViewportSize).</summary>
    public double VisibleBeats => (double)GetValue(VisibleBeatsProperty);

    private static readonly DependencyPropertyKey ScrollMaximumBeatsPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(ScrollMaximumBeats), typeof(double), typeof(PianoRollControl), new PropertyMetadata(0.0));
    public static readonly DependencyProperty ScrollMaximumBeatsProperty = ScrollMaximumBeatsPropertyKey.DependencyProperty;

    /// <summary>Furthest <see cref="ViewStartBeats"/> worth scrolling to: past the last note, the
    /// loop end and the playhead, plus a bar.</summary>
    public double ScrollMaximumBeats => (double)GetValue(ScrollMaximumBeatsProperty);

    public PianoRollControl()
    {
        ClipToBounds = true;
        Focusable = true;
        FocusVisualStyle = null;
        SizeChanged += (_, e) =>
        {
            bool first = e.PreviousSize.Height <= 0 && e.NewSize.Height > 0;
            UpdateNoteLayerClip();
            UpdateScrollRange();
            if (first)
                ScrollToNotes();
            RepositionAll();
        };
        Children.Add(_noteLayer);
        Children.Add(_marquee);
        Children.Add(_needleMarker);
        _noteLayer.Children.Add(_drawPreview);
        _noteLayer.Children.Add(_needle);
        Panel.SetZIndex(_noteLayer, 0);
        Panel.SetZIndex(_drawPreview, int.MaxValue - 1);
        Panel.SetZIndex(_needle, int.MaxValue);
        Panel.SetZIndex(_marquee, int.MaxValue - 1);
        Panel.SetZIndex(_needleMarker, int.MaxValue);

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(TimerIntervalMs) };
        _timer.Tick += (_, _) => OnTimerTick();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();

        ContextMenuOpening += OnContextMenuOpening;
        ContextMenu = new ContextMenu(); // items are filled in each time it opens
    }

    // ---- Editor wiring ----

    private static void OnEditorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PianoRollControl)d;
        if (e.OldValue is PianoRollViewModel oldEditor)
        {
            oldEditor.PropertyChanged -= control.Editor_PropertyChanged;
            oldEditor.GhostsChanged -= control.Editor_GhostsChanged;
            oldEditor.TracksReplaced -= control.Editor_TracksReplaced;
        }
        if (e.NewValue is PianoRollViewModel newEditor)
        {
            newEditor.PropertyChanged += control.Editor_PropertyChanged;
            newEditor.GhostsChanged += control.Editor_GhostsChanged;
            newEditor.TracksReplaced += control.Editor_TracksReplaced;
        }
        control.SubscribeSelectedNotes();
        control.RebuildAllBars(preserveView: false);
        control.ScrollToNotes();
        control.UpdateSounding();
    }

    /// <summary>Follows the selected track's note collection.</summary>
    private void SubscribeSelectedNotes()
    {
        if (_subscribedNotes is not null)
            _subscribedNotes.CollectionChanged -= Notes_CollectionChanged;
        _subscribedNotes = Editor?.RollNotes;
        if (_subscribedNotes is not null)
            _subscribedNotes.CollectionChanged += Notes_CollectionChanged;
    }

    private void Editor_GhostsChanged(object? sender, EventArgs e)
    {
        RestyleAllBars(); // the selected track's colour may have changed too
        InvalidateVisual();
    }

    private void Editor_TracksReplaced(object? sender, EventArgs e)
    {
        _zOrder.Clear();
        _zOrderCounter = 0;
        RebuildAllBars(preserveView: false);
        ScrollToNotes();
    }

    private void Editor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PianoRollViewModel.PositionBeats):
                FollowPlayhead();
                RepositionNeedle();
                UpdateScrollRange();
                break;
            case nameof(PianoRollViewModel.SoundingKeys):
            case nameof(PianoRollViewModel.SoundingNoteIds):
                UpdateSounding();
                break;
            case nameof(PianoRollViewModel.Tool):
                Cursor = null;
                break;
            case nameof(PianoRollViewModel.RollNotes):
                // Another track selected: its notes become the editable bars.
                CancelDrag();
                SubscribeSelectedNotes();
                RebuildAllBars(preserveView: true);
                InvalidateVisual();
                break;
            case nameof(PianoRollViewModel.PixelsPerBeat):
                UpdateScrollRange();
                RepositionAll();
                break;
            case nameof(PianoRollViewModel.LoopEndBeat):
                UpdateScrollRange();
                InvalidateVisual();
                break;
            case nameof(PianoRollViewModel.Grid):
            case nameof(PianoRollViewModel.IsLoopEnabled):
            case nameof(PianoRollViewModel.LoopStartBeat):
                InvalidateVisual();
                break;
        }
    }

    private void Notes_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (PianoRollNote note in e.NewItems)
                {
                    AddBarFor(note);
                    if (IsLive)
                        ScrollRowIntoView(note.MidiNumber);
                }
                UpdateScrollRange();
                break;
            case NotifyCollectionChangedAction.Reset:
                // The track was emptied (Clear / a live resync): forget z-order history.
                RebuildAllBars(preserveView: false);
                break;
            default:
                // Edits (replace/remove): rebuild, but keep the view, z-order and selection.
                RebuildAllBars(preserveView: true);
                break;
        }
    }

    private static void OnGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PianoRollControl)d;
        control.UpdateNoteLayerClip();
        control.UpdateScrollRange();
        control.RebuildAllBars(preserveView: true);
    }

    private static void OnViewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((PianoRollControl)d).RepositionAll();

    private static void OnIsLiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (PianoRollControl)d;
        if ((bool)e.NewValue)
            control.CancelDrag();
        control.Cursor = null;
        control.InvalidateVisual();
    }

    private bool CanEdit => !IsLive && Editor is { CanEdit: true };

    private IEnumerable<PianoRollNote> Notes => (IEnumerable<PianoRollNote>?)Editor?.RollNotes ?? Array.Empty<PianoRollNote>();

    // ---- Bars ----

    private void RebuildAllBars(bool preserveView)
    {
        List<Guid> keepSelection = preserveView ? _selectedNoteIds.ToList() : new List<Guid>();

        foreach (Rectangle bar in _barToNote.Keys.ToList())
            _noteLayer.Children.Remove(bar);
        _barToNote.Clear();
        _noteIdToBar.Clear();
        _selectedNoteIds.Clear();

        if (!preserveView)
        {
            _zOrder.Clear();
            _zOrderCounter = 0;
            ViewStartBeats = 0;
        }

        foreach (PianoRollNote note in Notes)
            AddBarFor(note);

        bool selectionChanged = false;
        foreach (Guid id in keepSelection)
        {
            if (_noteIdToBar.ContainsKey(id))
                AddToSelection(id);
            else
                selectionChanged = true;
        }

        UpdateScrollRange();
        RepositionAll();
        if (selectionChanged || (!preserveView && keepSelection.Count > 0))
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddBarFor(PianoRollNote note)
    {
        if (note.MidiNumber < MinNote || note.MidiNumber > MaxNote)
            return;

        var bar = new Rectangle { RadiusX = 3, RadiusY = 3 };
        _noteLayer.Children.Add(bar);
        _barToNote[bar] = note;
        _noteIdToBar[note.Id] = bar;

        if (!_zOrder.ContainsKey(note.Id))
            _zOrder[note.Id] = _zOrderCounter++; // first time seen (new or drawn note): lands on top
        Panel.SetZIndex(bar, _zOrder[note.Id]);

        UpdateBarStyle(bar, note);
        PositionBar(bar, note.StartBeat, note.DurationBeats, note.MidiNumber);
    }

    private void PositionBar(Rectangle bar, double startBeat, double durationBeats, int midiNumber)
    {
        double rowHeight = RowHeight();
        double inset = BarInset(rowHeight);
        SetLeft(bar, XForBeat(startBeat) + 1);
        bar.Width = Math.Max(2.0, durationBeats * PxPerBeat() - 2);
        SetTop(bar, YForRow(midiNumber) + inset);
        bar.Height = Math.Max(2.0, rowHeight - 2 * inset);
    }

    private void RepositionAll()
    {
        foreach ((Rectangle bar, PianoRollNote note) in _barToNote)
        {
            if (_dragMode is DragMode.Move && _dragOriginals.ContainsKey(note.Id))
                continue;
            if (_dragMode is DragMode.ResizeLeft or DragMode.ResizeRight && note.Id == _dragNoteId)
                continue;
            PositionBar(bar, note.StartBeat, note.DurationBeats, note.MidiNumber);
        }
        RepositionNeedle();
        InvalidateVisual();
    }

    private void UpdateBarStyle(Rectangle bar, PianoRollNote note)
    {
        bool selected = _selectedNoteIds.Contains(note.Id);
        bool sounding = _soundingNoteIds.Contains(note.Id);
        bool erasing = _erasing.Contains(note.Id);
        bar.Fill = selected ? FindThemeBrush("Pitch", "#5FB3E8") : TrackBrush();
        bar.Opacity = erasing ? 0.35 : 1.0;

        if (sounding)
        {
            bar.Stroke = FindThemeBrush("InkStrong", "#F4F2EE");
            bar.StrokeThickness = 1.5;
        }
        else if (selected)
        {
            bar.Stroke = FindThemeBrush("SelectionRing", "#DCEFFB");
            bar.StrokeThickness = 1.5;
        }
        else if (note.ChordGroupId is not null)
        {
            bar.Stroke = FindThemeBrush("BorderStrong", "#4A4E56");
            bar.StrokeThickness = 1.5;
        }
        else
        {
            bar.Stroke = null;
            bar.StrokeThickness = 0;
        }
    }

    private void RestyleAllBars()
    {
        foreach ((Rectangle bar, PianoRollNote note) in _barToNote)
            UpdateBarStyle(bar, note);
    }

    private void UpdateSounding()
    {
        _soundingNoteIds.Clear();
        _soundingKeys.Clear();
        if (Editor is { } editor)
        {
            _soundingNoteIds.UnionWith(editor.SoundingNoteIds);
            _soundingKeys.UnionWith(editor.SoundingKeys);
        }
        RestyleAllBars();
        InvalidateVisual();
    }

    // ---- Geometry ----

    /// <summary>The selected track's colour (Accent without an editor).</summary>
    private Brush TrackBrush() => Editor?.SelectedTrack is { } track ? track.ColourBrush : FindThemeBrush("Accent", "#F2A541");

    private double EffectiveVisibleBeats() => Math.Max(0.25, (ActualWidth - KeyboardWidth) / PxPerBeat());

    private double PxPerBeat() => Editor?.PixelsPerBeat ?? PianoRollViewModel.DefaultPixelsPerBeat;

    private double XForBeat(double beat) => KeyboardWidth + (beat - ViewStartBeats) * PxPerBeat();

    private double BeatForX(double x) => ViewStartBeats + (x - KeyboardWidth) / PxPerBeat();

    private static double RowHeight() => RowHeightPx;

    private const int RowCount = MaxNote - MinNote + 1;

    private double NoteAreaHeight() => Math.Max(0.0, ActualHeight - RulerHeight);

    private double MaxViewTop() => Math.Max(0.0, RowCount * RowHeightPx - NoteAreaHeight());

    /// <summary>Top edge of a note's row, in control coordinates.</summary>
    private double YForRow(int midiNumber) => RulerHeight + (MaxNote - midiNumber) * RowHeightPx - ViewTop;

    private int? MidiForY(double y)
    {
        if (y < RulerHeight)
            return null;
        int midi = MaxNote - (int)Math.Floor((y - RulerHeight + ViewTop) / RowHeightPx);
        return midi >= MinNote && midi <= MaxNote ? midi : null;
    }

    private int MidiForYClamped(double y)
    {
        int midi = MaxNote - (int)Math.Floor((y - RulerHeight + ViewTop) / RowHeightPx);
        return Math.Clamp(midi, MinNote, MaxNote);
    }

    private void UpdateNoteLayerClip()
    {
        _noteLayer.Width = Math.Max(0, ActualWidth);
        _noteLayer.Height = Math.Max(0, ActualHeight);
        _noteLayer.Clip = new RectangleGeometry(new Rect(KeyboardWidth, RulerHeight,
            Math.Max(0, ActualWidth - KeyboardWidth), Math.Max(0, ActualHeight - RulerHeight)));
    }

    /// <summary>Centres the view on every track's notes (or around C4 when there are none).</summary>
    public void ScrollToNotes()
    {
        List<PianoRollNote> all = Editor?.AllNotes.ToList() ?? new List<PianoRollNote>();
        double centreMidi = all.Count == 0 ? 60.5 : (all.Min(n => n.MidiNumber) + all.Max(n => n.MidiNumber) + 1) / 2.0;
        double centreY = (MaxNote + 1 - centreMidi) * RowHeightPx;
        ViewTop = centreY - NoteAreaHeight() / 2;
    }

    /// <summary>Scrolls just enough to show a row (a newly recorded note).</summary>
    private void ScrollRowIntoView(int midiNumber)
    {
        double top = (MaxNote - midiNumber) * RowHeightPx;
        double visible = NoteAreaHeight();
        if (visible <= 0)
            return;
        if (top < ViewTop)
            ViewTop = top - RowHeightPx;
        else if (top + RowHeightPx > ViewTop + visible)
            ViewTop = top + 2 * RowHeightPx - visible;
    }

    /// <summary>Arrow keys: Left/Right scroll a beat in time, Up/Down scroll a row. True when handled.</summary>
    public bool ScrollByKey(Key key)
    {
        switch (key)
        {
            case Key.Left:
                ViewStartBeats = Math.Max(0.0, ViewStartBeats - 1.0);
                return true;
            case Key.Right:
                ViewStartBeats = Math.Min(Math.Max(0.0, ScrollMaximumBeats), ViewStartBeats + 1.0);
                return true;
            case Key.Up:
                ViewTop -= RowHeightPx;
                return true;
            case Key.Down:
                ViewTop += RowHeightPx;
                return true;
        }
        return false;
    }

    private double ContentEndBeat() => RollEditing.EndBeat(Editor?.AllNotes ?? Notes);

    private void UpdateScrollRange()
    {
        SetValue(VisibleHeightPropertyKey, NoteAreaHeight());
        SetValue(VerticalScrollMaximumPropertyKey, MaxViewTop());
        CoerceValue(ViewTopProperty);
        double visible = EffectiveVisibleBeats();
        SetValue(VisibleBeatsPropertyKey, visible);
        double end = Math.Max(ContentEndBeat(), Editor?.LoopEndBeat ?? 0.0);
        end = Math.Max(end, Editor?.PositionBeats ?? 0.0);
        SetValue(ScrollMaximumBeatsPropertyKey, Math.Max(0.0, end + MusicalTime.BeatsPerBar - visible));
    }

    /// <summary>Scrolls so <paramref name="beat"/> sits at the left edge.</summary>
    public void ScrollToBeat(double beat) => ViewStartBeats = Math.Max(0.0, beat);

    /// <summary>While recording, keeps the moving playhead (the recording position) in view.</summary>
    private void OnTimerTick()
    {
        if (!IsLive || Editor is not { } editor)
            return;
        double visible = EffectiveVisibleBeats();
        double position = editor.PositionBeats;
        if (position > ViewStartBeats + visible * 0.85 || position < ViewStartBeats)
            ViewStartBeats = Math.Max(0.0, position - visible * 0.15);
    }

    /// <summary>While playing, pages forward when the playhead runs off the right edge (and back
    /// when a loop wraps out of view); the view otherwise stays where you put it.</summary>
    private void FollowPlayhead()
    {
        if (Editor is not { IsPlaying: true } editor || IsLive || _dragMode != DragMode.None)
            return;
        double visible = EffectiveVisibleBeats();
        if (editor.PositionBeats > ViewStartBeats + visible || editor.PositionBeats < ViewStartBeats)
            ViewStartBeats = Math.Max(0.0, editor.PositionBeats - visible * 0.1);
    }

    private void RepositionNeedle()
    {
        double position = Editor?.PositionBeats ?? 0.0;
        double x = XForBeat(position);
        bool visible = Editor is not null && x >= KeyboardWidth && x <= ActualWidth;
        _needle.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _needleMarker.Visibility = _needle.Visibility;
        if (!visible)
            return;
        _needle.Height = NoteAreaHeight();
        SetTop(_needle, RulerHeight);
        _needle.Fill = FindThemeBrush("InkStrong", "#F4F2EE");
        SetLeft(_needle, x - 1);
        SetTop(_needleMarker, RulerHeight - 10);
        SetLeft(_needleMarker, x - 7);
    }

    private GridDivision Grid => Editor?.Grid ?? GridDivisions.Default;

    private static bool SnapEnabled => !Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);

    private double Snap(double beat) => GridDivisions.Snap(beat, Grid, SnapEnabled);

    /// <summary>Shortest note a snapped edit leaves: one grid step (or the floor with snapping off).</summary>
    private double MinLength() =>
        SnapEnabled && GridDivisions.LengthBeats(Grid) is double step ? step : RollEditing.MinNoteBeats;

    // ---- Playable key column (INoteInputSource) ----

    /// <summary>Nothing to start - playing is driven by mouse events on the key column.</summary>
    public void Start() { }

    /// <summary>Releases a held key if the control is detached (e.g. app shutdown).</summary>
    public void Stop() => SetPressedKeyboardNote(null);

    private int? HitTestKeyboardNote(double y) => MidiForY(y);

    private void SetPressedKeyboardNote(int? note)
    {
        if (_pressedKeyboardNote == note)
            return;
        if (_pressedKeyboardNote is int oldNote)
            NoteOff?.Invoke(oldNote);
        _pressedKeyboardNote = note;
        if (note is int newNote)
            NoteOn?.Invoke(newNote, DefaultVelocity);
        InvalidateVisual();
    }

    private void SetHoverKeyboardNote(int? note)
    {
        if (_hoverKeyboardNote == note)
            return;
        _hoverKeyboardNote = note;
        InvalidateVisual();
    }

    // ---- Hit-testing (overlap-aware: topmost first, repeated same-spot clicks cycle underneath) ----

    private static bool HitTestBarBounds(Rectangle bar, Point pos)
    {
        double left = GetLeft(bar);
        double top = GetTop(bar);
        return pos.X >= left && pos.X <= left + bar.Width && pos.Y >= top && pos.Y <= top + bar.Height;
    }

    private IReadOnlyList<Guid> HitTestCandidatesAt(Point pos) =>
        _barToNote
            .Where(kvp => HitTestBarBounds(kvp.Key, pos))
            .OrderByDescending(kvp => _zOrder.TryGetValue(kvp.Value.Id, out int z) ? z : -1)
            .Select(kvp => kvp.Value.Id)
            .ToList();

    private Guid? HitTestClickTarget(Point pos)
    {
        IReadOnlyList<Guid> candidates = HitTestCandidatesAt(pos);
        if (candidates.Count == 0)
        {
            _lastHitTestClickPos = null;
            return null;
        }

        bool samePosAsLastClick = _lastHitTestClickPos is Point last
            && Math.Abs(last.X - pos.X) < ClickCycleTolerancePx
            && Math.Abs(last.Y - pos.Y) < ClickCycleTolerancePx;

        _lastHitTestCandidateIndex = samePosAsLastClick ? (_lastHitTestCandidateIndex + 1) % candidates.Count : 0;
        _lastHitTestClickPos = pos;
        return candidates[_lastHitTestCandidateIndex];
    }

    private void TouchZOrder(Guid noteId)
    {
        _zOrder[noteId] = _zOrderCounter++;
        if (_noteIdToBar.TryGetValue(noteId, out Rectangle? bar))
            Panel.SetZIndex(bar, _zOrder[noteId]);
    }

    /// <summary>Which edge (if any) of a bar a point is on.</summary>
    private static DragMode EdgeAt(Rectangle bar, Point pos)
    {
        double left = GetLeft(bar);
        double handle = Math.Min(ResizeHandleWidthPx, bar.Width / 3);
        if (pos.X >= left + bar.Width - handle)
            return DragMode.ResizeRight;
        if (pos.X <= left + handle)
            return DragMode.ResizeLeft;
        return DragMode.Move;
    }

    // ---- Loop handles ----

    private Rect LoopStartHandleRect()
    {
        double x = XForBeat(Editor?.LoopStartBeat ?? 0.0);
        return new Rect(x, RulerHeight - LoopHandleSize, LoopHandleSize, LoopHandleSize);
    }

    private Rect LoopEndHandleRect()
    {
        double x = XForBeat(Editor?.LoopEndBeat ?? MusicalTime.BeatsPerBar);
        return new Rect(x - LoopHandleSize, RulerHeight - LoopHandleSize, LoopHandleSize, LoopHandleSize);
    }

    // ---- Mouse ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point pos = e.GetPosition(this);
        _dragStart = pos;

        if (pos.X < KeyboardWidth)
        {
            if (pos.Y >= RulerHeight)
            {
                _isPlayingKeyboard = true;
                CaptureMouse();
                SetPressedKeyboardNote(HitTestKeyboardNote(pos.Y));
            }
            return;
        }

        if (Editor is null)
            return;

        if (pos.Y < RulerHeight)
        {
            if (LoopEndHandleRect().Contains(pos))
                BeginDrag(DragMode.LoopEnd);
            else if (LoopStartHandleRect().Contains(pos))
                BeginDrag(DragMode.LoopStart);
            else
            {
                BeginDrag(DragMode.Seek);
                SeekToX(pos.X);
            }
            return;
        }

        EditTool tool = CanEdit ? Editor.Tool : EditTool.Select;
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        Guid? hit = HitTestClickTarget(pos);

        switch (tool)
        {
            case EditTool.Erase:
                _erasing.Clear();
                BeginDrag(DragMode.Erase);
                EraseAt(pos);
                return;

            case EditTool.Split:
                if (hit is Guid splitId)
                {
                    SplitAt(splitId, pos);
                    return;
                }
                BeginDrag(DragMode.PendingClick);
                return;

            case EditTool.Draw when hit is null:
                BeginDrawDrag(pos);
                return;
        }

        // Select tool (or Draw on an existing note): select, then move/resize.
        if (hit is Guid noteId && _noteIdToBar.TryGetValue(noteId, out Rectangle? bar))
        {
            if (shift || ctrl)
            {
                ToggleSelection(noteId);
                _lastClickedNoteId = noteId;
                RaiseSelectionChanged();
                return;
            }

            if (!_selectedNoteIds.Contains(noteId))
            {
                ClearSelectionInternal();
                AddToSelection(noteId);
                RaiseSelectionChanged();
            }
            _lastClickedNoteId = noteId;
            if (CanEdit)
                BeginNoteDrag(noteId, bar, EdgeAt(bar, pos));
            return;
        }

        // Empty grid: marquee (Select) - a plain click sets the time position instead.
        _dragMarqueeAdditive = shift || ctrl;
        _marqueeBaseSelection = _dragMarqueeAdditive ? _selectedNoteIds.ToList() : new List<Guid>();
        BeginDrag(DragMode.Marquee);
    }

    private void BeginDrag(DragMode mode)
    {
        _dragMode = mode;
        CaptureMouse();
    }

    private void BeginNoteDrag(Guid noteId, Rectangle bar, DragMode mode)
    {
        PianoRollNote note = _barToNote[bar];
        _dragNoteId = noteId;
        _dragNote = note;
        _dragDeltaBeats = 0;
        _dragDeltaPitch = 0;
        _dragPreviewStart = note.StartBeat;
        _dragPreviewDuration = note.DurationBeats;
        _dragOriginals = mode == DragMode.Move
            ? _selectedNoteIds.Where(id => _noteIdToBar.ContainsKey(id)).ToDictionary(id => id, id => _barToNote[_noteIdToBar[id]])
            : new Dictionary<Guid, PianoRollNote>();
        TouchZOrder(noteId);
        BeginDrag(mode);
    }

    private void BeginDrawDrag(Point pos)
    {
        if (MidiForY(pos.Y) is not int midi)
            return;
        _drawMidi = midi;
        _drawStartBeat = Math.Max(0.0, GridDivisions.SnapDown(BeatForX(pos.X), Grid, SnapEnabled));
        _drawPreview.Fill = TrackBrush();
        _drawPreview.Opacity = 0.7;
        _drawPreview.Visibility = Visibility.Visible;
        UpdateDrawPreview(pos);
        BeginDrag(DragMode.Draw);
    }

    private (double Start, double Length) DrawSpan(Point pos)
    {
        double length = GridDivisions.NoteLengthBeats(Grid);
        if (Math.Abs(pos.X - _dragStart.X) >= DragThresholdPx)
        {
            double end = Snap(BeatForX(pos.X));
            length = Math.Max(MinLength(), end - _drawStartBeat);
        }
        return (_drawStartBeat, length);
    }

    private void UpdateDrawPreview(Point pos)
    {
        (double start, double length) = DrawSpan(pos);
        PositionBar(_drawPreview, start, length, _drawMidi);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point pos = e.GetPosition(this);
        SetHoverKeyboardNote(pos.X < KeyboardWidth && pos.Y >= RulerHeight ? HitTestKeyboardNote(pos.Y) : null);

        if (_isPlayingKeyboard)
        {
            SetPressedKeyboardNote(HitTestKeyboardNote(pos.Y));
            return;
        }

        switch (_dragMode)
        {
            case DragMode.None:
                UpdateHoverCursor(pos);
                return;
            case DragMode.Seek:
                SeekToX(pos.X);
                return;
            case DragMode.LoopStart:
            case DragMode.LoopEnd:
                DragLoopHandle(pos);
                return;
            case DragMode.Draw:
                UpdateDrawPreview(pos);
                return;
            case DragMode.Erase:
                EraseAt(pos);
                return;
            case DragMode.Marquee:
                UpdateMarquee(pos);
                return;
            case DragMode.Move:
                PreviewMove(pos);
                return;
            case DragMode.ResizeLeft:
            case DragMode.ResizeRight:
                PreviewResize(pos);
                return;
        }
    }

    private void PreviewMove(Point pos)
    {
        if (_dragNote is null)
            return;
        double rawStart = _dragNote.StartBeat + (pos.X - _dragStart.X) / PxPerBeat();
        double newStart = Math.Max(0.0, Snap(rawStart));
        _dragDeltaBeats = newStart - _dragNote.StartBeat;
        _dragDeltaPitch = (int)Math.Round((_dragStart.Y - pos.Y) / RowHeight()); // up = higher pitch

        // Keep every moved note on or after beat 0 and within the key range.
        double earliest = _dragOriginals.Values.Min(n => n.StartBeat);
        _dragDeltaBeats = Math.Max(_dragDeltaBeats, -earliest);
        int lowest = _dragOriginals.Values.Min(n => n.MidiNumber), highest = _dragOriginals.Values.Max(n => n.MidiNumber);
        _dragDeltaPitch = Math.Clamp(_dragDeltaPitch, MinNote - lowest, MaxNote - highest);

        foreach ((Guid id, PianoRollNote original) in _dragOriginals)
        {
            if (_noteIdToBar.TryGetValue(id, out Rectangle? bar))
                PositionBar(bar, original.StartBeat + _dragDeltaBeats, original.DurationBeats, original.MidiNumber + _dragDeltaPitch);
        }
    }

    private void PreviewResize(Point pos)
    {
        if (_dragNote is null || _dragNoteId is not Guid id || !_noteIdToBar.TryGetValue(id, out Rectangle? bar))
            return;
        double deltaBeats = (pos.X - _dragStart.X) / PxPerBeat();
        double minLength = MinLength();
        if (_dragMode == DragMode.ResizeRight)
        {
            double end = Snap(_dragNote.EndBeat + deltaBeats);
            _dragPreviewStart = _dragNote.StartBeat;
            _dragPreviewDuration = Math.Max(minLength, end - _dragNote.StartBeat);
        }
        else // left edge: the note's end stays put
        {
            double start = Math.Clamp(Snap(_dragNote.StartBeat + deltaBeats), 0.0, _dragNote.EndBeat - minLength);
            _dragPreviewStart = Math.Max(0.0, start);
            _dragPreviewDuration = _dragNote.EndBeat - _dragPreviewStart;
        }
        PositionBar(bar, _dragPreviewStart, _dragPreviewDuration, _dragNote.MidiNumber);
    }

    private void UpdateMarquee(Point pos)
    {
        if (Math.Abs(pos.X - _dragStart.X) < DragThresholdPx && Math.Abs(pos.Y - _dragStart.Y) < DragThresholdPx)
            return;

        double left = Math.Max(KeyboardWidth, Math.Min(pos.X, _dragStart.X));
        double right = Math.Max(pos.X, _dragStart.X);
        double top = Math.Max(RulerHeight, Math.Min(pos.Y, _dragStart.Y));
        double bottom = Math.Max(pos.Y, _dragStart.Y);
        _marquee.Stroke = FindThemeBrush("Pitch", "#5FB3E8");
        _marquee.Fill = new SolidColorBrush(Color.FromArgb(0x14, 0x5F, 0xB3, 0xE8)); // Pitch at 8%
        _marquee.Visibility = Visibility.Visible;
        SetLeft(_marquee, left);
        SetTop(_marquee, top);
        _marquee.Width = Math.Max(0, right - left);
        _marquee.Height = Math.Max(0, bottom - top);

        IReadOnlyList<Guid> inBox = RollEditing.InBox(
            _barToNote.Values, BeatForX(left), BeatForX(right), MidiForYClamped(top), MidiForYClamped(bottom - 0.01));
        ClearSelectionInternal();
        foreach (Guid id in _marqueeBaseSelection.Concat(inBox))
            AddToSelection(id);
        RaiseSelectionChanged();
    }

    private void EraseAt(Point pos)
    {
        if (!CanEdit)
            return;
        foreach (Guid id in HitTestCandidatesAt(pos))
        {
            if (_erasing.Add(id) && _noteIdToBar.TryGetValue(id, out Rectangle? bar))
                UpdateBarStyle(bar, _barToNote[bar]);
        }
    }

    private void SplitAt(Guid noteId, Point pos)
    {
        if (Editor is null || !CanEdit)
            return;
        double at = Snap(BeatForX(pos.X));
        if (Editor.SplitNote(noteId, at) is { } halves)
            SetSelection(new[] { halves.Left.Id, halves.Right.Id });
    }

    private void SeekToX(double x)
    {
        if (Editor is null)
            return;
        Editor.SeekTo(Math.Max(0.0, Snap(BeatForX(x))));
        UpdateScrollRange();
    }

    private void DragLoopHandle(Point pos)
    {
        if (Editor is null)
            return;
        double beat = Math.Max(0.0, Snap(BeatForX(pos.X)));
        if (_dragMode == DragMode.LoopStart)
            Editor.SetLoop(Math.Min(beat, Editor.LoopEndBeat - 1.0), Editor.LoopEndBeat);
        else
            Editor.SetLoop(Editor.LoopStartBeat, Math.Max(beat, Editor.LoopStartBeat + 1.0));
        UpdateScrollRange();
        InvalidateVisual();
    }

    private void UpdateHoverCursor(Point pos)
    {
        if (pos.X < KeyboardWidth)
        {
            Cursor = null;
            return;
        }
        if (pos.Y < RulerHeight)
        {
            Cursor = LoopStartHandleRect().Contains(pos) || LoopEndHandleRect().Contains(pos) ? Cursors.SizeWE : null;
            return;
        }
        if (!CanEdit || Editor is null)
        {
            Cursor = null;
            return;
        }

        Guid? top = HitTestCandidatesAt(pos).FirstOrDefault();
        bool overNote = top is Guid id && id != Guid.Empty && _noteIdToBar.ContainsKey(id);
        Cursor = Editor.Tool switch
        {
            EditTool.Draw when !overNote => Cursors.Pen,
            EditTool.Erase => overNote ? Cursors.Hand : null,
            EditTool.Split => overNote ? Cursors.IBeam : null,
            _ when overNote => EdgeAt(_noteIdToBar[top!.Value], pos) == DragMode.Move ? Cursors.SizeAll : Cursors.SizeWE,
            _ => null,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        Point pos = e.GetPosition(this);

        if (_isPlayingKeyboard)
        {
            _isPlayingKeyboard = false;
            ReleaseMouseCapture();
            SetPressedKeyboardNote(null);
            return;
        }

        DragMode mode = _dragMode;
        _dragMode = DragMode.None;
        ReleaseMouseCapture();
        bool moved = Math.Abs(pos.X - _dragStart.X) >= DragThresholdPx || Math.Abs(pos.Y - _dragStart.Y) >= DragThresholdPx;

        switch (mode)
        {
            case DragMode.Draw:
                _drawPreview.Visibility = Visibility.Collapsed;
                if (Editor is not null && CanEdit)
                {
                    (double start, double length) = DrawSpan(pos);
                    PianoRollNote added = Editor.AddNote(start, length, _drawMidi);
                    SetSelection(new[] { added.Id });
                }
                break;

            case DragMode.Erase:
                if (Editor is not null && _erasing.Count > 0)
                {
                    var ids = _erasing.ToList();
                    _erasing.Clear();
                    Editor.DeleteNotes(ids);
                }
                break;

            case DragMode.Marquee:
                _marquee.Visibility = Visibility.Collapsed;
                if (!moved)
                {
                    if (!_dragMarqueeAdditive && _selectedNoteIds.Count > 0)
                    {
                        ClearSelectionInternal();
                        RaiseSelectionChanged();
                    }
                    SeekToX(pos.X);
                }
                break;

            case DragMode.PendingClick:
                if (!moved)
                    SeekToX(pos.X);
                break;

            case DragMode.Move:
                if (Editor is not null && (_dragDeltaBeats != 0 || _dragDeltaPitch != 0))
                {
                    IReadOnlyList<PianoRollNote> results = Editor.MoveNotes(_dragOriginals.Keys.ToList(), _dragDeltaBeats, _dragDeltaPitch);
                    SetSelection(results.Select(n => n.Id));
                }
                else
                {
                    RepositionAll();
                    // A plain click on one note of a multi-selection selects just that note.
                    if (!moved && _dragNoteId is Guid clicked && _selectedNoteIds.Count > 1)
                        SetSelection(new[] { clicked });
                }
                break;

            case DragMode.ResizeLeft:
            case DragMode.ResizeRight:
                if (Editor is not null && _dragNoteId is Guid resized && _dragNote is not null
                    && (Math.Abs(_dragPreviewStart - _dragNote.StartBeat) > 1e-9 || Math.Abs(_dragPreviewDuration - _dragNote.DurationBeats) > 1e-9))
                {
                    PianoRollNote? result = Editor.ResizeNote(resized, _dragPreviewStart, _dragPreviewDuration);
                    if (result is not null)
                        SetSelection(new[] { result.Id });
                }
                else
                {
                    RepositionAll();
                }
                break;
        }

        _dragNote = null;
        _dragNoteId = null;
        _dragOriginals = new Dictionary<Guid, PianoRollNote>();
    }

    /// <summary>Drops an in-progress gesture without committing it (e.g. recording starts).</summary>
    private void CancelDrag()
    {
        if (_dragMode == DragMode.None)
            return;
        _dragMode = DragMode.None;
        ReleaseMouseCapture();
        _drawPreview.Visibility = Visibility.Collapsed;
        _marquee.Visibility = Visibility.Collapsed;
        _erasing.Clear();
        _dragOriginals = new Dictionary<Guid, PianoRollNote>();
        _dragNote = null;
        _dragNoteId = null;
        RestyleAllBars();
        RepositionAll();
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_dragMode != DragMode.None && Mouse.LeftButton != MouseButtonState.Pressed)
            CancelDrag();
    }

    /// <summary>Safety net against a stuck note if the mouse leaves while a key is held.</summary>
    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetHoverKeyboardNote(null);
        if (_isPlayingKeyboard)
        {
            _isPlayingKeyboard = false;
            ReleaseMouseCapture();
            SetPressedKeyboardNote(null);
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        Point pos = e.GetPosition(this);
        if (pos.X < KeyboardWidth || pos.Y < RulerHeight)
            return;
        // Right-clicking an unselected note selects just that note, like most editors.
        if (HitTestCandidatesAt(pos).FirstOrDefault() is Guid id && id != Guid.Empty && !_selectedNoteIds.Contains(id))
            SetSelection(new[] { id });
    }

    // ---- Right-click menu ----

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        Point pos = Mouse.GetPosition(this);
        if (Editor is not { } editor || pos.X < KeyboardWidth || pos.Y < RulerHeight)
        {
            e.Handled = true;
            return;
        }

        bool hasSelection = _selectedNoteIds.Count > 0;
        ContextMenu menu = ContextMenu ?? (ContextMenu = new ContextMenu());
        if (TryFindResource("EditMenuPanel") is Style panelStyle)
            menu.Style = panelStyle;
        menu.Items.Clear();

        menu.Items.Add(MenuEntry("Cut", "Ctrl+X", hasSelection && CanEdit, CutSelection));
        menu.Items.Add(MenuEntry("Copy", "Ctrl+C", hasSelection, CopySelection));
        menu.Items.Add(MenuEntry($"Paste at {MusicalTime.FormatPosition(editor.PositionBeats)}", "Ctrl+V", editor.CanPaste && CanEdit, PasteAtPosition));
        MenuItem delete = MenuEntry("Delete", "Del", hasSelection && CanEdit, DeleteSelection);
        delete.Foreground = FindThemeBrush("Record", "#F0625F");
        menu.Items.Add(delete);
        var separator = new Separator();
        if (TryFindResource("EditMenuSeparator") is Style separatorStyle)
            separator.Style = separatorStyle;
        menu.Items.Add(separator);
        menu.Items.Add(MenuEntry("Select all", "Ctrl+A", _barToNote.Count > 0, SelectAll));
    }

    private MenuItem MenuEntry(string header, string shortcut, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, InputGestureText = shortcut, IsEnabled = enabled };
        if (TryFindResource("EditMenuEntry") is Style style)
            item.Style = style;
        item.Click += (_, _) => action();
        return item;
    }

    // ---- Commands (menu and keyboard) ----

    public void SelectAll() => SetSelection(_barToNote.Values.Select(n => n.Id));

    public void CopySelection() => Editor?.Copy(_selectedNoteIds.ToList());

    public void CutSelection()
    {
        if (Editor is null || !CanEdit || _selectedNoteIds.Count == 0)
            return;
        Editor.Cut(_selectedNoteIds.ToList());
    }

    public void DeleteSelection()
    {
        if (Editor is null || !CanEdit || _selectedNoteIds.Count == 0)
            return;
        Editor.DeleteNotes(_selectedNoteIds.ToList());
    }

    public void PasteAtPosition()
    {
        if (Editor is null || !CanEdit)
            return;
        IReadOnlyList<PianoRollNote> pasted = Editor.Paste();
        if (pasted.Count > 0)
            SetSelection(pasted.Select(n => n.Id));
    }

    /// <summary>The piano roll's keyboard shortcuts, for the tab to call from PreviewKeyDown
    /// (skipped while typing in a text box): V/D/E/S tools, Delete, Ctrl+A/C/X/V, Ctrl+Z undo,
    /// Ctrl+Y or Ctrl+Shift+Z redo, Esc clears the selection. True when handled.</summary>
    public bool HandleShortcut(Key key, ModifierKeys modifiers)
    {
        if (Editor is not { } editor)
            return false;

        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        bool shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool alt = modifiers.HasFlag(ModifierKeys.Alt);
        if (alt)
            return false;

        if (ctrl)
        {
            switch (key)
            {
                case Key.A: SelectAll(); return true;
                case Key.C: CopySelection(); return true;
                case Key.X: CutSelection(); return true;
                case Key.V: PasteAtPosition(); return true;
                case Key.Z when shift: return Execute(editor.RedoCommand);
                case Key.Z: return Execute(editor.UndoCommand);
                case Key.Y: return Execute(editor.RedoCommand);
            }
            return false;
        }

        if (shift)
            return false;

        switch (key)
        {
            case Key.Delete:
            case Key.Back:
                DeleteSelection();
                return true;
            case Key.Escape when _selectedNoteIds.Count > 0:
                SetSelection(Array.Empty<Guid>());
                return true;
            case Key.V: editor.Tool = EditTool.Select; return true;
            case Key.D: editor.Tool = EditTool.Draw; return true;
            case Key.E: editor.Tool = EditTool.Erase; return true;
            case Key.S: editor.Tool = EditTool.Split; return true;
        }
        return false;
    }

    private static bool Execute(ICommand command)
    {
        if (command.CanExecute(null))
            command.Execute(null);
        return true;
    }

    // ---- Selection ----

    /// <summary>Replaces the selection (e.g. the new chord voices, or pasted notes).</summary>
    public void SetSelection(IEnumerable<Guid> noteIds)
    {
        ClearSelectionInternal();
        foreach (Guid noteId in noteIds)
            AddToSelection(noteId);
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        InvalidateVisual(); // selected-note bars on the keys
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void AddToSelection(Guid noteId)
    {
        if (_selectedNoteIds.Contains(noteId) || !_noteIdToBar.TryGetValue(noteId, out Rectangle? bar))
            return;
        _selectedNoteIds.Add(noteId);
        UpdateBarStyle(bar, _barToNote[bar]);
    }

    private void ToggleSelection(Guid noteId)
    {
        if (_selectedNoteIds.Remove(noteId))
        {
            if (_noteIdToBar.TryGetValue(noteId, out Rectangle? bar))
                UpdateBarStyle(bar, _barToNote[bar]);
        }
        else
        {
            AddToSelection(noteId);
        }
    }

    private void ClearSelectionInternal()
    {
        if (_selectedNoteIds.Count == 0)
            return;
        var previous = _selectedNoteIds.ToList();
        _selectedNoteIds.Clear();
        foreach (Guid noteId in previous)
        {
            if (_noteIdToBar.TryGetValue(noteId, out Rectangle? bar))
                UpdateBarStyle(bar, _barToNote[bar]);
        }
    }

    // ---- Zoom and pan ----

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ModifierKeys modifiers = Keyboard.Modifiers;

        if (modifiers.HasFlag(ModifierKeys.Alt))
        {
            // Zoom time around the mouse position (remembered per tab).
            if (Editor is not { } editor)
                return;
            double anchorX = Math.Max(KeyboardWidth, e.GetPosition(this).X);
            double anchorBeat = BeatForX(anchorX);
            double factor = e.Delta > 0 ? 1.0 / 0.9 : 0.9;
            editor.PixelsPerBeat = editor.PixelsPerBeat * factor;
            ViewStartBeats = Math.Max(0.0, anchorBeat - (anchorX - KeyboardWidth) / PxPerBeat());
            UpdateScrollRange();
            RepositionAll();
            e.Handled = true;
        }
        else if (modifiers.HasFlag(ModifierKeys.Control))
        {
            ViewTop -= e.Delta / 120.0 * RowHeightPx * 3; // wheel forward = higher notes
            e.Handled = true;
        }
        else if (!IsLive)
        {
            double deltaBeats = -e.Delta / 120.0 * EffectiveVisibleBeats() / 10.0; // wheel forward = earlier
            ViewStartBeats = Math.Clamp(ViewStartBeats + deltaBeats, 0.0, Math.Max(0.0, ScrollMaximumBeats));
            e.Handled = true;
        }
    }

    // ---- Drawing ----

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        // Design/KeyColumn.dc.html and the Live Tuner / File Analysis mockups
        Brush background = FindThemeBrush("Bg", "#0F1012");
        Brush laneWhite = FindThemeBrush("LaneWhite", "#1A1C20");
        Brush laneBlack = FindThemeBrush("LaneBlack", "#141518");
        Brush pitchLane = FindThemeBrush("PitchLane", "#295FB3E8");
        Brush laneDivider = FindThemeBrush("SurfaceRaised", "#1F2125");
        Brush columnDivider = FindThemeBrush("Border", "#34373D");
        Brush keyWhite = FindThemeBrush("KeyWhite", "#D9D6CF");
        Brush keyBlack = FindThemeBrush("KeyBlack", "#2A2C31");
        Brush keyWhiteHover = FindThemeBrush("KeyWhiteHover", "#ECEAE4");
        Brush keyBlackHover = FindThemeBrush("KeyBlackHover", "#3A3D44");
        Brush keyLabelWhite = FindThemeBrush("KeyLabel", "#3A3D44");
        Brush keyLabelBlack = FindThemeBrush("InkMuted", "#C9CBD0");
        Brush keyLabelBlackHover = FindThemeBrush("Ink", "#E8E6E1");
        Brush pitch = FindThemeBrush("Pitch", "#5FB3E8");
        Brush onPitch = FindThemeBrush("OnPitch", "#0B1A24");
        Brush accent = FindThemeBrush("Accent", "#F2A541");
        Brush onAccent = FindThemeBrush("OnAccent", "#1A1307");
        Brush inkStrong = FindThemeBrush("InkStrong", "#F4F2EE");
        _needle.Fill = inkStrong;
        _needleMarker.Fill = inkStrong;

        double width = Math.Max(0, ActualWidth);
        double height = Math.Max(0, ActualHeight);
        dc.DrawRectangle(background, null, new Rect(0, 0, width, height));
        if (width <= 0 || height <= 0)
            return;

        double rowHeight = RowHeight();
        // Only the rows in view (top to bottom).
        int topMidi = Math.Min(MaxNote, MaxNote - (int)Math.Floor(ViewTop / rowHeight));
        int bottomMidi = Math.Max(MinNote, MaxNote - (int)Math.Floor((ViewTop + NoteAreaHeight()) / rowHeight));
        var typeface = new Typeface(FindMonoFamily(), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var labelTypeface = new Typeface(FindMonoFamily(), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        bool fullLabels = rowHeight >= FullLabelMinRowHeight;
        double labelSize = fullLabels ? 12.0 : Math.Max(6.0, Math.Min(10.0, rowHeight - 2));
        HashSet<int> selectedKeys = SelectedMidiNumbers();
        var dividerPen = new Pen(laneDivider, 1);

        // Lanes, the time grid, the loop wash and the other tracks' ghost notes, all under the notes
        dc.PushClip(new RectangleGeometry(new Rect(0, RulerHeight, width, Math.Max(0, height - RulerHeight))));
        for (int midiNumber = topMidi; midiNumber >= bottomMidi; midiNumber--)
        {
            double y = YForRow(midiNumber);
            var lane = new Rect(KeyboardWidth, y, Math.Max(0, width - KeyboardWidth), rowHeight);
            dc.DrawRectangle(IsBlackKey(midiNumber) ? laneBlack : laneWhite, null, lane);
            if (midiNumber == DetectedNote)
                dc.DrawRectangle(pitchLane, null, lane);
            dc.DrawLine(dividerPen, new Point(KeyboardWidth, y + rowHeight - 0.5), new Point(width, y + rowHeight - 0.5));
        }
        DrawTimeGrid(dc, width, height);
        DrawLoopWash(dc, height);
        DrawGhostNotes(dc, width, height);

        // Key column
        for (int midiNumber = topMidi; midiNumber >= bottomMidi; midiNumber--)
        {
            bool isBlackKey = IsBlackKey(midiNumber);
            bool isDetected = midiNumber == DetectedNote;
            bool isSounding = midiNumber == _pressedKeyboardNote || _soundingKeys.Contains(midiNumber);
            bool isHover = midiNumber == _hoverKeyboardNote;
            double y = YForRow(midiNumber);
            double keyHeight = Math.Max(0, rowHeight - 1); // 1px Bg gap between keys

            Brush keyFill = isBlackKey ? keyBlack : keyWhite;
            Brush labelFill = isBlackKey ? keyLabelBlack : keyLabelWhite;
            if (isHover)
            {
                keyFill = isBlackKey ? keyBlackHover : keyWhiteHover;
                labelFill = isBlackKey ? keyLabelBlackHover : keyLabelWhite;
            }
            if (isSounding) { keyFill = accent; labelFill = onAccent; }
            if (isDetected) { keyFill = pitch; labelFill = onPitch; }

            double keyWidth = isBlackKey && !isDetected && !isSounding ? BlackKeyWidth(KeyboardWidth) : KeyboardWidth;
            dc.DrawRectangle(keyFill, null, new Rect(0, y, keyWidth, keyHeight));

            if (ColorByPitchClass)
                dc.DrawRectangle(new SolidColorBrush(ColorForPitchClass(((midiNumber % 12) + 12) % 12)), null,
                    new Rect(0, y, Math.Min(PitchClassStripWidth, keyWidth), keyHeight));

            if (selectedKeys.Contains(midiNumber))
                dc.DrawRectangle(isDetected ? onPitch : pitch, null,
                    new Rect(KeyboardWidth - SelectedKeyBarWidth, y, SelectedKeyBarWidth, keyHeight));

            if (fullLabels || midiNumber % 12 == 0 || isDetected)
            {
                var text = new FormattedText(KeyLabel(midiNumber), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, isDetected || isSounding ? labelTypeface : typeface, labelSize, labelFill, pixelsPerDip);
                double right = keyWidth - 8;
                double textLeft = right - text.Width;
                dc.DrawText(text, new Point(textLeft, y + (keyHeight - text.Height) / 2));

                if (isHover && fullLabels && TryFindResource("IconSpeaker") is Geometry speaker)
                    DrawSpeaker(dc, speaker, labelFill, textLeft - 4 - 12, y + (keyHeight - 12) / 2);
            }
        }

        dc.DrawLine(new Pen(columnDivider, 1), new Point(KeyboardWidth - 0.5, RulerHeight), new Point(KeyboardWidth - 0.5, height));
        dc.Pop();

        DrawRuler(dc, width, pixelsPerDip);
    }

    /// <summary>The other tracks' notes, in their colours at 22%, clipped to the note area. Not
    /// editable or selectable.</summary>
    private void DrawGhostNotes(DrawingContext dc, double width, double height)
    {
        if (Editor is not { } editor || editor.Tracks.Count < 2)
            return;
        double left = BeatForX(KeyboardWidth), right = BeatForX(width);
        double pxPerBeat = PxPerBeat();
        double inset = BarInset(RowHeightPx);
        dc.PushClip(new RectangleGeometry(new Rect(KeyboardWidth, RulerHeight, Math.Max(0, width - KeyboardWidth), Math.Max(0, height - RulerHeight))));
        foreach (Track track in editor.Tracks)
        {
            if (ReferenceEquals(track, editor.SelectedTrack) || track.Notes.Count == 0)
                continue;
            Color c = track.Colour;
            var brush = new SolidColorBrush(Color.FromArgb(GhostAlpha, c.R, c.G, c.B));
            brush.Freeze();
            foreach (PianoRollNote note in track.Notes)
            {
                if (note.EndBeat < left || note.StartBeat > right)
                    continue;
                double y = YForRow(note.MidiNumber);
                if (y + RowHeightPx < RulerHeight || y > height)
                    continue;
                dc.DrawRoundedRectangle(brush, null, new Rect(XForBeat(note.StartBeat) + 1, y + inset,
                    Math.Max(2.0, note.DurationBeats * pxPerBeat - 2), Math.Max(2.0, RowHeightPx - 2 * inset)), 3, 3);
            }
        }
        dc.Pop();
    }

    private static bool IsBlackKey(int midiNumber) => (((midiNumber % 12) + 12) % 12) is 1 or 3 or 6 or 8 or 10;

    /// <summary>Black keys are 26px shorter than white keys; in a narrow column, proportionally shorter.</summary>
    internal static double BlackKeyWidth(double columnWidth) =>
        columnWidth >= 2 * BlackKeyShortening ? columnWidth - BlackKeyShortening : columnWidth * 0.64;

    /// <summary>3px above and below a note in a 24px row (the design), never more than 15% of a
    /// smaller row.</summary>
    internal static double BarInset(double rowHeight) => Math.Min(3.0, rowHeight * 0.15);

    /// <summary>Key name with "#" for sharps: 61 -> "C#4" (C4 = 60).</summary>
    internal static string KeyLabel(int midiNumber)
    {
        (string name, int octave) = NoteQuantizer.GetNoteNameAndOctave(midiNumber);
        return name + octave;
    }

    /// <summary>Every how many bars to label the ruler so labels keep at least
    /// <see cref="MinBarLabelSpacingPx"/> apart: 1, 2, 4, 8...</summary>
    internal static int BarLabelStep(double pxPerBar)
    {
        int step = 1;
        while (step * pxPerBar < MinBarLabelSpacingPx && step < 1024)
            step *= 2;
        return step;
    }

    private HashSet<int> SelectedMidiNumbers()
    {
        var keys = new HashSet<int>();
        foreach ((Rectangle _, PianoRollNote note) in _barToNote)
        {
            if (_selectedNoteIds.Contains(note.Id))
                keys.Add(note.MidiNumber);
        }
        return keys;
    }

    private static void DrawSpeaker(DrawingContext dc, Geometry icon, Brush stroke, double left, double top)
    {
        // Icon geometry is on a 24 x 24 grid; draw it at 12 x 12 with a 2.2px stroke (1.1 after scaling).
        dc.PushTransform(new TranslateTransform(left, top));
        dc.PushTransform(new ScaleTransform(0.5, 0.5));
        dc.DrawGeometry(null, new Pen(stroke, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, icon);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>Grid lines across the note area: bars BorderStrong, beats Border, and the Grid
    /// setting's subdivisions SurfaceRaised (when finer than a beat and far enough apart to read).</summary>
    private void DrawTimeGrid(DrawingContext dc, double width, double height)
    {
        var barPen = new Pen(FindThemeBrush("BorderStrong", "#4A4E56"), 1);
        var beatPen = new Pen(FindThemeBrush("Border", "#34373D"), 1);
        var subPen = new Pen(FindThemeBrush("SurfaceRaised", "#1F2125"), 1);
        double pxPerBeat = PxPerBeat();
        double first = Math.Floor(ViewStartBeats);
        double last = BeatForX(width);

        if (GridDivisions.LengthBeats(Grid) is double step && step < 1.0 && step * pxPerBeat >= MinGridLineSpacingPx)
        {
            int perBeat = (int)Math.Round(1.0 / step);
            for (double beat = first; beat <= last; beat += 1.0)
            {
                for (int k = 1; k < perBeat; k++)
                    DrawGridLine(dc, subPen, beat + k * step, width, height);
            }
        }

        bool drawBeats = pxPerBeat >= MinGridLineSpacingPx;
        for (double beat = first; beat <= last; beat += 1.0)
        {
            bool isBar = Math.Abs(beat % MusicalTime.BeatsPerBar) < 1e-9;
            if (isBar)
                DrawGridLine(dc, barPen, beat, width, height);
            else if (drawBeats)
                DrawGridLine(dc, beatPen, beat, width, height);
        }
    }

    private void DrawGridLine(DrawingContext dc, Pen pen, double beat, double width, double height)
    {
        double x = Math.Round(XForBeat(beat)) + 0.5;
        if (x < KeyboardWidth || x > width)
            return;
        dc.DrawLine(pen, new Point(x, RulerHeight), new Point(x, height));
    }

    private void DrawLoopWash(DrawingContext dc, double height)
    {
        if (Editor is not { IsLoopEnabled: true } editor)
            return;
        double left = Math.Max(KeyboardWidth, XForBeat(editor.LoopStartBeat));
        double right = Math.Min(ActualWidth, XForBeat(editor.LoopEndBeat));
        if (right <= left)
            return;
        Color accent = FindThemeColor("AccentColor", "#F2A541");
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x0F, accent.R, accent.G, accent.B)), null,
            new Rect(left, RulerHeight, right - left, height - RulerHeight)); // 6%
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(0x99, accent.R, accent.G, accent.B)), 1); // 60%
        if (XForBeat(editor.LoopStartBeat) >= KeyboardWidth)
            dc.DrawLine(edge, new Point(left + 0.5, RulerHeight), new Point(left + 0.5, height));
        if (XForBeat(editor.LoopEndBeat) <= ActualWidth)
            dc.DrawLine(edge, new Point(right - 0.5, RulerHeight), new Point(right - 0.5, height));
    }

    /// <summary>Bars and beats only: bar numbers with a bar line, beat ticks along the bottom, the
    /// loop band and S/E handles, styled per the mockups (BgChrome strip, 13px mono labels).</summary>
    private void DrawRuler(DrawingContext dc, double width, double pixelsPerDip)
    {
        Brush strip = FindThemeBrush("BgChrome", "#131417");
        Brush tick = FindThemeBrush("BorderStrong", "#4A4E56");
        Brush barLine = FindThemeBrush("InkFaint", "#6B6F77");
        Brush divider = FindThemeBrush("Border", "#34373D");
        Brush labelBrush = FindThemeBrush("Ink", "#E8E6E1");

        dc.DrawRectangle(strip, null, new Rect(0, 0, width, RulerHeight));

        double pxPerBeat = PxPerBeat();
        var clip = new RectangleGeometry(new Rect(KeyboardWidth, 0, Math.Max(0, width - KeyboardWidth), RulerHeight));
        dc.PushClip(clip);

        // Loop band
        if (Editor is { IsLoopEnabled: true } loopEditor)
        {
            Color accent = FindThemeColor("AccentColor", "#F2A541");
            double left = XForBeat(loopEditor.LoopStartBeat), right = XForBeat(loopEditor.LoopEndBeat);
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(0x38, accent.R, accent.G, accent.B)), null, // 22%
                new Rect(left, RulerHeight - LoopBandHeight, Math.Max(0, right - left), LoopBandHeight));
        }

        // Beat ticks and bar lines with numbers
        var tickPen = new Pen(tick, 1);
        var barPen = new Pen(barLine, 1);
        var labelFace = new Typeface(FindMonoFamily(), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
        int labelStep = BarLabelStep(pxPerBeat * MusicalTime.BeatsPerBar);
        double first = Math.Floor(ViewStartBeats);
        double last = BeatForX(width);
        for (double beat = first; beat <= last; beat += 1.0)
        {
            double x = Math.Round(XForBeat(beat)) + 0.5;
            bool isBar = Math.Abs(beat % MusicalTime.BeatsPerBar) < 1e-9;
            if (!isBar)
            {
                if (pxPerBeat >= MinGridLineSpacingPx)
                    dc.DrawLine(tickPen, new Point(x, RulerHeight - BeatTickHeight), new Point(x, RulerHeight));
                continue;
            }

            int barIndex = (int)Math.Round(beat / MusicalTime.BeatsPerBar);
            if (barIndex % labelStep != 0)
            {
                dc.DrawLine(tickPen, new Point(x, RulerHeight - BeatTickHeight), new Point(x, RulerHeight));
                continue;
            }
            dc.DrawLine(barPen, new Point(x, 0), new Point(x, RulerHeight));
            var text = new FormattedText((barIndex + 1).ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, labelFace, 13, labelBrush, pixelsPerDip);
            dc.DrawText(text, new Point(x + 6, 5));
        }

        // Loop handles: Accent with Loop on, grey with it off
        if (Editor is { } editor)
        {
            bool on = editor.IsLoopEnabled;
            Brush handleFill = on ? FindThemeBrush("Accent", "#F2A541") : FindThemeBrush("BorderStrong", "#4A4E56");
            Brush handleText = on ? FindThemeBrush("OnAccent", "#1A1307") : FindThemeBrush("Ink", "#E8E6E1");
            var handleFace = new Typeface(FindMonoFamily(), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            DrawLoopHandle(dc, LoopStartHandleRect(), "S", topRightRounded: true, handleFill, handleText, handleFace, pixelsPerDip);
            DrawLoopHandle(dc, LoopEndHandleRect(), "E", topRightRounded: false, handleFill, handleText, handleFace, pixelsPerDip);
        }

        dc.Pop();

        dc.DrawLine(new Pen(divider, 1), new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
        dc.DrawLine(new Pen(divider, 1), new Point(KeyboardWidth - 0.5, 0), new Point(KeyboardWidth - 0.5, RulerHeight));
    }

    private static void DrawLoopHandle(DrawingContext dc, Rect rect, string label, bool topRightRounded,
        Brush fill, Brush textBrush, Typeface face, double pixelsPerDip)
    {
        // One rounded top corner (4px) on the inside of the region, like the mockup.
        var geometry = new StreamGeometry();
        using (StreamGeometryContext g = geometry.Open())
        {
            const double r = 4;
            if (topRightRounded)
            {
                g.BeginFigure(new Point(rect.Left, rect.Bottom), true, true);
                g.LineTo(new Point(rect.Left, rect.Top), true, false);
                g.LineTo(new Point(rect.Right - r, rect.Top), true, false);
                g.ArcTo(new Point(rect.Right, rect.Top + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                g.LineTo(new Point(rect.Right, rect.Bottom), true, false);
            }
            else
            {
                g.BeginFigure(new Point(rect.Left, rect.Bottom), true, true);
                g.LineTo(new Point(rect.Left, rect.Top + r), true, false);
                g.ArcTo(new Point(rect.Left + r, rect.Top), new Size(r, r), 0, false, SweepDirection.Clockwise, true, false);
                g.LineTo(new Point(rect.Right, rect.Top), true, false);
                g.LineTo(new Point(rect.Right, rect.Bottom), true, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(fill, null, geometry);
        var text = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, 11, textBrush, pixelsPerDip);
        dc.DrawText(text, new Point(rect.Left + (rect.Width - text.Width) / 2, rect.Top + (rect.Height - text.Height) / 2));
    }

    private Brush FindThemeBrush(string resourceKey, string fallbackHex)
    {
        if (TryFindResource(resourceKey) is Brush brush)
            return brush;
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);
    }

    private Color FindThemeColor(string resourceKey, string fallbackHex) =>
        TryFindResource(resourceKey) is Color color ? color : (Color)ColorConverter.ConvertFromString(fallbackHex)!;

    private FontFamily FindMonoFamily() =>
        TryFindResource("Mono") as FontFamily ?? new FontFamily("Consolas");

    /// <summary>Evenly spaced hues around the color wheel, one per pitch class (0=C).</summary>
    internal static Color ColorForPitchClass(int pitchClass) => PitchClassColors[((pitchClass % 12) + 12) % 12];

    private static Color[] BuildPitchClassColors()
    {
        var colors = new Color[12];
        for (int i = 0; i < 12; i++)
            colors[i] = HslToColor(i / 12.0 * 360.0, 0.65, 0.55);
        return colors;
    }

    private static Color HslToColor(double hueDegrees, double saturation, double lightness)
    {
        double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double h = hueDegrees / 60.0;
        double x = c * (1 - Math.Abs(h % 2 - 1));
        double m = lightness - c / 2;

        (double r, double g, double b) = h switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    // ---- Test hooks (xUnit has no live visual tree or real mouse events) ----

    /// <summary>Count of note bars only (excludes the needle, marquee and draw preview).</summary>
    internal int BarCountForTest => _barToNote.Count;

    internal void ArrangeForTest(double width, double height)
    {
        Measure(new Size(width, height));
        Arrange(new Rect(0, 0, width, height));
        UpdateNoteLayerClip();
        UpdateScrollRange();
        RepositionAll();
    }

    internal double XForBeatForTest(double beat) => XForBeat(beat);
    internal double YForMidiForTest(int midi) => YForRow(midi) + RowHeightPx / 2;

    internal IReadOnlyList<Guid> HitTestNoteBarsForTest(Point pos) => HitTestCandidatesAt(pos);
    internal Guid? SimulateClickForTest(Point pos) => HitTestClickTarget(pos);
    internal int? HitTestKeyboardNoteForTest(double y) => HitTestKeyboardNote(y);
    internal int? PressedKeyboardNoteForTest => _pressedKeyboardNote;
    internal void SimulateKeyboardPressForTest(int? note) => SetPressedKeyboardNote(note);
    internal int? ZOrderForTest(Guid noteId) => _zOrder.TryGetValue(noteId, out int z) ? z : null;
    internal bool IsSoundingForTest(Guid noteId) => _soundingNoteIds.Contains(noteId);
    internal bool IsKeySoundingForTest(int midi) => _soundingKeys.Contains(midi);
    internal Brush? BarStrokeForTest(Guid noteId) => _noteIdToBar.TryGetValue(noteId, out Rectangle? bar) ? bar.Stroke : null;

    /// <summary>Selects what a marquee from one (beat, pitch) corner to the other would.</summary>
    internal void SimulateMarqueeForTest(double beatA, int midiA, double beatB, int midiB, bool additive = false)
    {
        IReadOnlyList<Guid> inBox = RollEditing.InBox(_barToNote.Values, beatA, beatB, midiA, midiB);
        if (!additive)
            ClearSelectionInternal();
        foreach (Guid id in inBox)
            AddToSelection(id);
        RaiseSelectionChanged();
    }
}
