using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ToneDetector.App.Controls;

namespace ToneDetector.App.Views;

/// <summary>Routes a tab's key presses to its piano roll: Ctrl shortcuts (undo, redo, clipboard,
/// select all) anywhere in the tab except a text box; single keys (tools, Delete, Esc) only while
/// the roll itself has focus, so they don't fight the computer-keyboard piano or list navigation.
/// The arrow keys always scroll the roll (they never move notes), except while something that uses
/// them itself has focus: a text box, list, drop-down, slider or pan control.</summary>
internal static class PianoRollShortcuts
{
    public static void Handle(KeyEventArgs e, PianoRollControl roll)
    {
        if (e.Handled || Keyboard.FocusedElement is TextBox)
            return;

        ModifierKeys modifiers = Keyboard.Modifiers;
        if (IsArrow(e.Key) && modifiers == ModifierKeys.None)
        {
            if (!UsesArrowKeys(Keyboard.FocusedElement as DependencyObject) && roll.ScrollByKey(e.Key))
                e.Handled = true;
            return;
        }

        bool rollFocused = ReferenceEquals(Keyboard.FocusedElement, roll);
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control);
        if (!ctrl && !rollFocused)
            return;
        if (ctrl && !rollFocused && Keyboard.FocusedElement is ComboBox)
            return;

        if (roll.HandleShortcut(e.Key, modifiers))
            e.Handled = true;
    }

    internal static bool IsArrow(Key key) => key is Key.Left or Key.Right or Key.Up or Key.Down;

    /// <summary>True for focused controls whose own arrow-key behaviour wins over scrolling.</summary>
    internal static bool UsesArrowKeys(DependencyObject? focused) =>
        focused is TextBoxBase or ComboBox or ComboBoxItem or ListBox or ListBoxItem or Slider or RangeBase or PanControl;
}
