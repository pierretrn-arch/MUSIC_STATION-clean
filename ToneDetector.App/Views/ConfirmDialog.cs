using System.Windows;

namespace ToneDetector.App.Views;

/// <summary>Yes/No questions before something that can't be undone (replacing or closing unsaved tracks).</summary>
internal static class ConfirmDialog
{
    public static bool Ask(DependencyObject owner, string question)
    {
        Window? window = Window.GetWindow(owner);
        MessageBoxResult result = window is null
            ? MessageBox.Show(question, "Music Station", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(window, question, "Music Station", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}
