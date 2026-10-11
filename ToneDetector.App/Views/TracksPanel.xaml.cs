using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ToneDetector.App.ViewModels;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.Views;

/// <summary>The tracks panel shared by Live Tuner and File Analysis. Renames, colour changes and
/// deletes go through <see cref="PianoRollViewModel"/> so they can be undone; mute, solo, volume
/// and pan bind straight to the track.</summary>
public partial class TracksPanel : UserControl
{
    public static readonly DependencyProperty RollProperty = DependencyProperty.Register(
        nameof(Roll), typeof(PianoRollViewModel), typeof(TracksPanel), new PropertyMetadata(null));

    public TracksPanel()
    {
        InitializeComponent();
    }

    /// <summary>The tab's piano roll, which owns the tracks.</summary>
    public PianoRollViewModel? Roll
    {
        get => (PianoRollViewModel?)GetValue(RollProperty);
        set => SetValue(RollProperty, value);
    }

    private static Track? TrackOf(object sender) => (sender as FrameworkElement)?.DataContext as Track;

    /// <summary>A click anywhere on a card (its controls included) edits that track.</summary>
    private void Card_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Roll is { } roll && TrackOf(sender) is { } track)
            roll.SelectedTrack = track;
    }

    // ---- Rename ----

    private void NameText_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && sender is TextBlock text)
        {
            BeginRename(text);
            e.Handled = true;
        }
    }

    private static void BeginRename(TextBlock nameText)
    {
        if (nameText.Parent is not Grid grid || grid.Children.OfType<TextBox>().FirstOrDefault() is not { } box
            || nameText.DataContext is not Track track)
            return;
        box.Text = track.Name;
        box.Tag = null;
        nameText.Visibility = Visibility.Collapsed;
        box.Visibility = Visibility.Visible;
        box.Focus();
        box.SelectAll();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box)
            return;
        switch (e.Key)
        {
            case Key.Enter:
                EndRename(box, commit: true);
                e.Handled = true;
                break;
            case Key.Escape:
                EndRename(box, commit: false);
                e.Handled = true;
                break;
        }
    }

    private void NameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { Visibility: Visibility.Visible } box)
            EndRename(box, commit: true);
    }

    private void EndRename(TextBox box, bool commit)
    {
        if (box.Visibility != Visibility.Visible)
            return;
        box.Visibility = Visibility.Collapsed;
        if (box.Parent is Grid grid && grid.Children.OfType<TextBlock>().FirstOrDefault() is { } nameText)
            nameText.Visibility = Visibility.Visible;
        if (commit && Roll is { } roll && box.DataContext is Track track)
            roll.RenameTrack(track, box.Text);
    }

    // ---- Right-click menu: Rename, Colour, Delete ----

    private void Card_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (Roll is not { } roll || sender is not Border card || TrackOf(sender) is not { } track || card.ContextMenu is not { } menu)
        {
            e.Handled = true;
            return;
        }

        roll.SelectedTrack = track;
        if (TryFindResource("EditMenuPanel") is Style panelStyle)
            menu.Style = panelStyle;
        menu.Items.Clear();

        MenuItem rename = Entry("Rename", roll.CanEdit, () =>
        {
            if (FindName(card, "NameText") is TextBlock nameText)
                BeginRename(nameText);
        });
        menu.Items.Add(rename);

        // Colour: the eight track colours as swatches in one row (the menu style has no submenus).
        var swatches = new StackPanel { Orientation = Orientation.Horizontal };
        swatches.Children.Add(new TextBlock { Text = "Colour", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
        for (int i = 0; i < TrackPalette.Colours.Count; i++)
        {
            int index = i;
            var swatch = new Button
            {
                Width = 20,
                Height = 20,
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                ToolTip = TrackPalette.Names[i],
                IsEnabled = roll.CanEdit,
                Template = SwatchTemplate(Track.BrushOf(i), selected: i == track.ColourIndex),
            };
            System.Windows.Automation.AutomationProperties.SetName(swatch, TrackPalette.Names[i]);
            swatch.Click += (_, _) =>
            {
                menu.IsOpen = false;
                roll.SetTrackColour(track, index);
            };
            swatches.Children.Add(swatch);
        }
        MenuItem colour = Entry("", true, null);
        colour.Header = swatches;
        colour.StaysOpenOnClick = true;
        menu.Items.Add(colour);

        var separator = new Separator();
        if (TryFindResource("EditMenuSeparator") is Style separatorStyle)
            separator.Style = separatorStyle;
        menu.Items.Add(separator);

        MenuItem delete = Entry("Delete track", roll.CanDeleteTrack, () => roll.DeleteTrack(track));
        delete.Foreground = TryFindResource("Record") as Brush ?? Brushes.IndianRed;
        if (!roll.CanDeleteTrack)
            delete.ToolTip = "A tab always keeps one track";
        menu.Items.Add(delete);
    }

    private MenuItem Entry(string header, bool enabled, Action? action)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (TryFindResource("EditMenuEntry") is Style style)
            item.Style = style;
        if (action is not null)
            item.Click += (_, _) => action();
        return item;
    }

    /// <summary>A rounded colour square; the current colour gets an InkStrong ring.</summary>
    private ControlTemplate SwatchTemplate(Brush fill, bool selected)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
        border.SetValue(Border.BackgroundProperty, fill);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(selected ? 2 : 0));
        border.SetValue(Border.BorderBrushProperty, TryFindResource("InkStrong") as Brush ?? Brushes.White);
        template.VisualTree = border;
        return template;
    }

    /// <summary>Finds a named element in a card built from the data template.</summary>
    private static object? FindName(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var childName } element && childName == name)
                return element;
            if (FindName(child, name) is { } found)
                return found;
        }
        return null;
    }
}
