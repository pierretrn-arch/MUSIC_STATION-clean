using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ToneDetector.App.Views;

/// <summary>
/// Home screen. Navigation lives in <see cref="MainWindow"/> (which owns the tabs), so "Open piano
/// roll" only raises an event here.
/// </summary>
public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    /// <summary>"Open piano roll": the editor is currently the Live Tuner tab's Piano Roll sub-tab.</summary>
    public event EventHandler? OpenPianoRollRequested;

    private void OpenPianoRollButton_Click(object sender, RoutedEventArgs e) =>
        OpenPianoRollRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Border's CornerRadius doesn't clip its content, so clip the preview to the tile's
    /// rounded corners by hand.</summary>
    private void PreviewWell_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double radius = PreviewWell.CornerRadius.TopLeft;
        PreviewWell.Clip = new RectangleGeometry(new Rect(e.NewSize), radius, radius);
    }
}
