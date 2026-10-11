using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToneDetector.App.Controls;
using ToneDetector.App.ViewModels;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Views;

/// <summary>File Analysis screen. The piano roll edits <see cref="FileAnalysisViewModel.Roll"/>
/// itself; the shared tool bar, transport and Tempo box bind to the same roll. MainWindow attaches
/// <see cref="Roll"/> as a note input source so its keys play.</summary>
public partial class FileAnalysisView : UserControl
{
    public FileAnalysisView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is MainViewModel viewModel)
            {
                viewModel.FileAnalysis.ChooseMidiParts = ChooseMidiParts;
                viewModel.FileAnalysis.Roll.Confirm = question => ConfirmDialog.Ask(this, question);
            }
        };
    }

    /// <summary>The piano roll, for MainWindow to attach as a note input source.</summary>
    public PianoRollControl Roll => PianoRoll;

    private void View_PreviewKeyDown(object sender, KeyEventArgs e) => PianoRollShortcuts.Handle(e, PianoRoll);

    /// <summary>Asks which parts of a multi-part MIDI file to load.</summary>
    private IReadOnlyList<MidiPart>? ChooseMidiParts(MidiFileContents contents)
    {
        var dialog = new MidiPartsDialog(contents) { Owner = Window.GetWindow(this) };
        return dialog.ShowDialog() == true ? dialog.SelectedParts : null;
    }

    private void CollapseResults_Click(object sender, RoutedEventArgs e) => SetResultsCollapsed(true);

    private void ExpandResults_Click(object sender, RoutedEventArgs e) => SetResultsCollapsed(false);

    private void SetResultsCollapsed(bool collapsed)
    {
        ResultsPanel.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        ExpandResultsButton.Visibility = collapsed ? Visibility.Visible : Visibility.Collapsed;
        (collapsed ? ExpandResultsButton : (UIElement)ResultsPanel).Focus();
    }
}
