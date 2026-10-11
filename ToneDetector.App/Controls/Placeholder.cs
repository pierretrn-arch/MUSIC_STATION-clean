using System.Windows;

namespace ToneDetector.App.Controls;

/// <summary>
/// Placeholder text for TextBoxes using the TextInput style (Themes/Controls.xaml), shown while the
/// box is empty: <c>controls:Placeholder.Text="Type a project name"</c>.
/// </summary>
public static class Placeholder
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Placeholder), new PropertyMetadata(string.Empty));

    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}
