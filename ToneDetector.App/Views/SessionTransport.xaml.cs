using System.Windows;
using System.Windows.Controls;
using ToneDetector.App.ViewModels;

namespace ToneDetector.App.Views;

/// <summary>Session-bar transport shared by Live Tuner and File Analysis.</summary>
public partial class SessionTransport : UserControl
{
    public static readonly DependencyProperty RollProperty = DependencyProperty.Register(
        nameof(Roll), typeof(PianoRollViewModel), typeof(SessionTransport), new PropertyMetadata(null));

    public SessionTransport()
    {
        InitializeComponent();
    }

    /// <summary>The tab's piano roll (its notes, loop and playhead).</summary>
    public PianoRollViewModel? Roll
    {
        get => (PianoRollViewModel?)GetValue(RollProperty);
        set => SetValue(RollProperty, value);
    }
}
