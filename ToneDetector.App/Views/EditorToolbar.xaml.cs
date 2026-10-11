using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ToneDetector.App.Controls;
using ToneDetector.App.ViewModels;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Views;

/// <summary>The piano roll's editor tool bar, shared by Live Tuner and File Analysis.</summary>
public partial class EditorToolbar : UserControl
{
    public const string HelpText =
        "Tools: Select (V) - click a note to select it, Shift-click to add, drag on empty space for a box, drag a " +
        "note's body to move the selection or an edge to resize. Draw (D) - click for a note one grid step long, or " +
        "drag to set its length. Erase (E) - click or drag across notes. Split (S) - click a note to split it there. " +
        "Moving, resizing, drawing, splitting and the loop handles snap to the Grid; hold Alt to turn snapping off. " +
        "Click the ruler or empty grid to set the time position; paste goes there. Right-click for Cut, Copy, Paste " +
        "and Delete. Ctrl+Z undo, Ctrl+Y redo, Ctrl+A select all. Editing is off while recording.";

    public static readonly DependencyProperty RollProperty = DependencyProperty.Register(
        nameof(Roll), typeof(PianoRollViewModel), typeof(EditorToolbar), new PropertyMetadata(null, OnRollChanged));

    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(PianoRollControl), typeof(EditorToolbar), new PropertyMetadata(null, OnTargetChanged));

    public EditorToolbar()
    {
        InitializeComponent();
        UpdateSelectionUi();
    }

    /// <summary>The tab's piano roll.</summary>
    public PianoRollViewModel? Roll
    {
        get => (PianoRollViewModel?)GetValue(RollProperty);
        set => SetValue(RollProperty, value);
    }

    /// <summary>The tab's piano roll control (for the selection).</summary>
    public PianoRollControl? Target
    {
        get => (PianoRollControl?)GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>Keeps each tab's tool buttons in their own radio group.</summary>
    public string ToolGroupName { get; } = "Tools" + Guid.NewGuid().ToString("N");

    /// <summary>"3 notes selected", or empty with no selection.</summary>
    public static string SelectionSummary(int count) =>
        count == 0 ? "" : count == 1 ? "1 note selected" : $"{count} notes selected";

    private static void OnRollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toolbar = (EditorToolbar)d;
        if (e.OldValue is PianoRollViewModel oldRoll)
            oldRoll.PropertyChanged -= toolbar.Roll_PropertyChanged;
        if (e.NewValue is PianoRollViewModel newRoll)
            newRoll.PropertyChanged += toolbar.Roll_PropertyChanged;
        toolbar.UpdateSelectionUi();
    }

    private static void OnTargetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var toolbar = (EditorToolbar)d;
        if (e.OldValue is PianoRollControl oldTarget)
            oldTarget.SelectionChanged -= toolbar.Target_SelectionChanged;
        if (e.NewValue is PianoRollControl newTarget)
            newTarget.SelectionChanged += toolbar.Target_SelectionChanged;
        toolbar.UpdateSelectionUi();
    }

    private void Roll_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PianoRollViewModel.CanEdit))
            UpdateSelectionUi();
    }

    private void Target_SelectionChanged(object? sender, EventArgs e) => UpdateSelectionUi();

    /// <summary>Selection count, and Apply only with a selection and while editing is allowed.</summary>
    private void UpdateSelectionUi()
    {
        int count = Target?.SelectedNoteIds.Count ?? 0;
        SelectionText.Text = SelectionSummary(count);
        ApplyChordButton.IsEnabled = count > 0 && Roll?.CanEdit == true;
    }

    /// <summary>Applies the chosen chord quality to every selected note, then selects the new
    /// voices so a follow-up transform or Undo is easy.</summary>
    private void ApplyChordButton_Click(object sender, RoutedEventArgs e)
    {
        if (Roll is not { } roll || Target is not { } target || target.SelectedNoteIds.Count == 0
            || ChordQualityComboBox.SelectedItem is not string qualityName)
            return;

        var quality = Enum.Parse<ChordQuality>(qualityName);
        IReadOnlyList<PianoRollNote> voices = roll.ApplyChordTransform(target.SelectedNoteIds.ToList(), quality);
        target.SetSelection(voices.Select(v => v.Id));
    }

    private void HelpPopup_Closed(object? sender, EventArgs e) => HelpToggle.IsChecked = false;
}
