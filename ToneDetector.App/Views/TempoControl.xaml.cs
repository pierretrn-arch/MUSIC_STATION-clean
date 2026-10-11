using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToneDetector.App.ViewModels;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Views;

/// <summary>The session bar's Tempo box, shared by Live Tuner and File Analysis.</summary>
public partial class TempoControl : UserControl
{
    public static readonly DependencyProperty RollProperty = DependencyProperty.Register(
        nameof(Roll), typeof(PianoRollViewModel), typeof(TempoControl), new PropertyMetadata(null, OnRollChanged));

    public TempoControl()
    {
        InitializeComponent();
    }

    /// <summary>The tab's piano roll, which owns the tempo.</summary>
    public PianoRollViewModel? Roll
    {
        get => (PianoRollViewModel?)GetValue(RollProperty);
        set => SetValue(RollProperty, value);
    }

    private static void OnRollChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (TempoControl)d;
        if (e.OldValue is PianoRollViewModel oldRoll)
            oldRoll.PropertyChanged -= control.Roll_PropertyChanged;
        if (e.NewValue is PianoRollViewModel newRoll)
            newRoll.PropertyChanged += control.Roll_PropertyChanged;
        control.ShowTempo();
    }

    private void Roll_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PianoRollViewModel.Bpm))
            ShowTempo();
    }

    private void ShowTempo() =>
        TempoBox.Text = (Roll?.Bpm ?? MusicalTime.DefaultBpm).ToString("0", CultureInfo.InvariantCulture);

    /// <summary>Applies what was typed (clamped to 40-240); anything unreadable puts the current tempo back.</summary>
    internal static double? ParseTempo(string text) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) ? MusicalTime.ClampBpm(bpm) : null;

    private void Commit()
    {
        if (Roll is { } roll && ParseTempo(TempoBox.Text) is double bpm)
            roll.Bpm = bpm;
        ShowTempo();
    }

    private void TempoBox_KeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Commit();
                TempoBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape:
                ShowTempo();
                e.Handled = true;
                break;
            case Key.Up when Roll is { } up:
                up.Bpm += 1;
                e.Handled = true;
                break;
            case Key.Down when Roll is { } down:
                down.Bpm -= 1;
                e.Handled = true;
                break;
        }
    }

    private void TempoBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => Commit();

    private void TempoBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => TempoBox.SelectAll();

    private void TempoBox_PreviewTextInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !e.Text.All(char.IsDigit);
}
