using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ToneDetector.App.ViewModels;

namespace ToneDetector.App.Views;

/// <summary>The tuner panel shared by Live Tuner and File Analysis. Each tab binds
/// <see cref="IsExpanded"/> and <see cref="PanelHeight"/> to its own saved settings.</summary>
public partial class TunerPanel : UserControl
{
    public static readonly DependencyProperty IsExpandedProperty = DependencyProperty.Register(
        nameof(IsExpanded), typeof(bool), typeof(TunerPanel),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty PanelHeightProperty = DependencyProperty.Register(
        nameof(PanelHeight), typeof(double), typeof(TunerPanel),
        new FrameworkPropertyMetadata(VisualizationSettingsViewModel.DefaultTunerHeight, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public TunerPanel()
    {
        InitializeComponent();
    }

    /// <summary>Open (the full panel) or collapsed (the 48px strip).</summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Height of the open panel, set with the drag handle.</summary>
    public double PanelHeight
    {
        get => (double)GetValue(PanelHeightProperty);
        set => SetValue(PanelHeightProperty, value);
    }

    // SetCurrentValue keeps the two-way binding to the saved setting.
    private void Collapse_Click(object sender, RoutedEventArgs e) => SetCurrentValue(IsExpandedProperty, false);

    private void Expand_Click(object sender, RoutedEventArgs e) => SetCurrentValue(IsExpandedProperty, true);

    private void ResizeHandle_DragDelta(object sender, DragDeltaEventArgs e) =>
        SetCurrentValue(PanelHeightProperty, VisualizationSettingsViewModel.ClampTunerHeight(PanelHeight + e.VerticalChange));
}
