using System.Windows;
using System.Windows.Controls;

namespace ToneDetector.App.Views;

/// <summary>
/// Settings screen. Opening the plugin editor needs the owning window, so the button only raises
/// <see cref="ShowPluginEditorRequested"/> and <see cref="MainWindow"/> does the rest.
/// </summary>
public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler? ShowPluginEditorRequested;

    private void ShowPluginEditorButton_Click(object sender, RoutedEventArgs e) =>
        ShowPluginEditorRequested?.Invoke(this, EventArgs.Empty);
}
