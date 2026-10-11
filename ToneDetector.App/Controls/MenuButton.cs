using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ToneDetector.App.Controls;

/// <summary>
/// Makes a Button open its own ContextMenu below itself on click (Export ▾):
/// <c>controls:MenuButton.OpensContextMenu="True"</c>. The menu keeps the button's DataContext, so
/// its items can bind commands as usual.
/// </summary>
public static class MenuButton
{
    public static readonly DependencyProperty OpensContextMenuProperty = DependencyProperty.RegisterAttached(
        "OpensContextMenu", typeof(bool), typeof(MenuButton), new PropertyMetadata(false, OnOpensContextMenuChanged));

    public static bool GetOpensContextMenu(DependencyObject element) => (bool)element.GetValue(OpensContextMenuProperty);

    public static void SetOpensContextMenu(DependencyObject element, bool value) => element.SetValue(OpensContextMenuProperty, value);

    private static void OnOpensContextMenuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button)
            return;
        button.Click -= OnClick;
        if (e.NewValue is true)
            button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not ButtonBase { ContextMenu: { } menu } button)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = 4;
        menu.IsOpen = true;
    }
}
