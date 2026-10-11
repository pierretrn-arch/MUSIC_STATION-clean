using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ToneDetector.App.ViewModels;

/// <summary>
/// Home screen state that isn't the live tuner's. Projects don't exist yet, so the Projects panel
/// is inactive: <see cref="RecentProjects"/> stays empty and New/Open/Continue are disabled in the
/// view. When a project model lands, its items replace <c>object</c> here and the view gains an
/// item template.
/// </summary>
public sealed class HomeViewModel : INotifyPropertyChanged
{
    public const string ProjectsUnavailableText = "Projects aren't available yet";

    public HomeViewModel()
    {
        RecentProjects.CollectionChanged += OnRecentProjectsChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<object> RecentProjects { get; } = new();

    public bool HasRecentProjects => RecentProjects.Count > 0;

    private string _projectSearchText = "";
    /// <summary>Bound to the search box. Nothing to filter until projects exist.</summary>
    public string ProjectSearchText
    {
        get => _projectSearchText;
        set
        {
            if (_projectSearchText == value)
                return;
            _projectSearchText = value;
            OnPropertyChanged();
        }
    }

    private void OnRecentProjectsChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        OnPropertyChanged(nameof(HasRecentProjects));

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
