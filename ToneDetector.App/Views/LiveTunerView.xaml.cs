using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ToneDetector.App.Controls;
using ToneDetector.App.ViewModels;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Views;

/// <summary>
/// Live Tuner screen. The piano roll edits <see cref="MainViewModel.PianoRoll"/> itself; the
/// shared tool bar, transport, Tempo box and tracks panel bind to the same roll. The side panel's
/// Grid lists the selected track's notes and mirrors the roll's selection both ways; Notation shows
/// the selected track. MainWindow attaches <see cref="Roll"/> as a note input source.
/// </summary>
public partial class LiveTunerView : UserControl
{
    private MainViewModel? _viewModel;
    private bool _syncingSelection;

    public LiveTunerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>The piano roll, for MainWindow to attach as a note input source.</summary>
    public PianoRollControl Roll => PianoRoll;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.LiveTuner.PropertyChanged -= LiveTuner_PropertyChanged;
            _viewModel.PianoRoll.SelectedNotesChanged -= SelectedNotes_Changed;
            _viewModel.PianoRoll.PropertyChanged -= PianoRoll_PropertyChanged;
            _viewModel.PianoRoll.Confirm = null;
        }

        _viewModel = e.NewValue as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.LiveTuner.PropertyChanged += LiveTuner_PropertyChanged;
            _viewModel.PianoRoll.SelectedNotesChanged += SelectedNotes_Changed;
            _viewModel.PianoRoll.PropertyChanged += PianoRoll_PropertyChanged;
            _viewModel.PianoRoll.Confirm = question => ConfirmDialog.Ask(this, question);
        }
        RefreshGrid();
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e) => PianoRollShortcuts.Handle(e, PianoRoll);

    /// <summary>The Grid shows ms at the current tempo; its caption names the selected track
    /// (a rename or its Undo raises the edit state).</summary>
    private void PianoRoll_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PianoRollViewModel.Bpm))
            RefreshGrid();
        else if (e.PropertyName is nameof(PianoRollViewModel.CanUndoEdit) or nameof(PianoRollViewModel.CanRedoEdit))
            UpdateGridCaption();
    }

    /// <summary>When recording stops, scrolls the roll back to the bar the take started in.
    /// Deferred one dispatcher pass so the roll's own IsLive binding, which listens to the same
    /// event, has settled first. Hide silence rows refreshes the Grid.</summary>
    private void LiveTuner_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_viewModel is null)
            return;
        if (e.PropertyName == nameof(LiveTunerViewModel.HideSilenceRows))
        {
            RefreshGrid();
            return;
        }
        if (e.PropertyName != nameof(LiveTunerViewModel.IsRecording) || _viewModel.LiveTuner.IsRecording)
            return;

        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            double takeStart = _viewModel.PianoRoll.LastTakeStartBeat;
            PianoRoll.ScrollToBeat(Math.Floor(takeStart / MusicalTime.BeatsPerBar) * MusicalTime.BeatsPerBar);
        });
    }

    // ---- Side panel Grid ----

    private void SelectedNotes_Changed(object? sender, EventArgs e) => RefreshGrid();

    private void RefreshGrid()
    {
        IReadOnlyList<RollGridRow> rows = _viewModel is null
            ? Array.Empty<RollGridRow>()
            : RollGridRow.Build(_viewModel.PianoRoll.RollNotes, _viewModel.PianoRoll.Bpm, _viewModel.LiveTuner.HideSilenceRows);

        _syncingSelection = true;
        NoteList.ItemsSource = rows;
        _syncingSelection = false;

        UpdateGridCaption();
        EmptyGridText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MirrorRollSelectionInGrid();
    }

    private void UpdateGridCaption()
    {
        int noteCount = _viewModel?.PianoRoll.RollNotes.Count ?? 0;
        string name = _viewModel?.PianoRoll.SelectedTrack.Name ?? "";
        GridCaption.Text = $"{name} · {RollGridRow.CountText(noteCount)}";
    }

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSelection)
            return;

        // Silence rows expand but don't select anything on the roll.
        var ids = NoteList.SelectedItems.Cast<RollGridRow>().Where(r => !r.IsSilence).Select(r => r.NoteId).ToList();
        _syncingSelection = true;
        PianoRoll.SetSelection(ids);
        _syncingSelection = false;
    }

    private void MirrorRollSelectionInGrid()
    {
        if (NoteList.ItemsSource is not IReadOnlyList<RollGridRow> rows)
            return;

        var selected = new HashSet<Guid>(PianoRoll.SelectedNoteIds);
        _syncingSelection = true;
        NoteList.SelectedItems.Clear();
        RollGridRow? first = null;
        foreach (RollGridRow row in rows)
        {
            if (!selected.Contains(row.NoteId))
                continue;
            NoteList.SelectedItems.Add(row);
            first ??= row;
        }
        if (first is not null)
            NoteList.ScrollIntoView(first);
        _syncingSelection = false;
    }

    // ---- Piano roll ----

    private void PianoRoll_SelectionChanged(object? sender, EventArgs e)
    {
        if (!_syncingSelection)
            MirrorRollSelectionInGrid();
    }

    // ---- Chrome ----

    /// <summary>Creates the notation view on first use, as the old Notation sub-tab did.</summary>
    private void NotationViewToggle_Checked(object sender, RoutedEventArgs e) =>
        NotationHost.Content ??= _viewModel?.PianoRoll;

    private void CollapseSidePanel_Click(object sender, RoutedEventArgs e) => SetSidePanelCollapsed(true);

    private void ExpandSidePanel_Click(object sender, RoutedEventArgs e) => SetSidePanelCollapsed(false);

    private void SetSidePanelCollapsed(bool collapsed)
    {
        SidePanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ExpandSidePanelButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        (collapsed ? ExpandSidePanelButton : (UIElement)SidePanel).Focus();
    }
}
