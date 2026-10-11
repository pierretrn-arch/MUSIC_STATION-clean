using System.Windows;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Views;

/// <summary>Picks which parts of a MIDI file to load (drums start unticked).</summary>
public partial class MidiPartsDialog : Window
{
    private readonly List<PartOption> _options;

    public MidiPartsDialog(MidiFileContents contents)
    {
        InitializeComponent();
        _options = contents.Parts.Select(p => new PartOption(p) { IsSelected = !p.IsDrums }).ToList();
        if (_options.All(o => !o.IsSelected))
            _options.ForEach(o => o.IsSelected = true); // a drums-only file
        PartList.ItemsSource = _options;
        UpdateLoadButton();
    }

    public IReadOnlyList<MidiPart> SelectedParts => _options.Where(o => o.IsSelected).Select(o => o.Part).ToList();

    private void Part_Changed(object sender, RoutedEventArgs e) => UpdateLoadButton();

    private void UpdateLoadButton() => LoadButton.IsEnabled = _options.Any(o => o.IsSelected);

    private void Load_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>One row: the part and whether to load it.</summary>
    public sealed class PartOption
    {
        public PartOption(MidiPart part) => Part = part;

        public MidiPart Part { get; }
        public bool IsSelected { get; set; }
        public string Name => Part.Name;
        public string Detail => Part.Notes.Count == 1 ? "1 note" : $"{Part.Notes.Count} notes";
    }
}
