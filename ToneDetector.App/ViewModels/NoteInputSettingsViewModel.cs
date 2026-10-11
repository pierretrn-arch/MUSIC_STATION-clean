using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using ToneDetector.Audio.Input;
using ToneDetector.Audio.Synth;

namespace ToneDetector.App.ViewModels;

/// <summary>One selectable entry in the MIDI device picker; <see cref="Index"/> of -1 is the
/// synthetic "(None)" option used to detach without picking a real device.</summary>
public sealed record MidiDeviceOption(int Index, string Name);

/// <summary>
/// Settings for the Change 9 note-input/playback features (USB MIDI, computer keyboard, on-screen
/// virtual keyboard, master volume). Holds only the settings data — attaching/detaching the actual
/// <see cref="ToneDetector.Audio.Input.INoteInputSource"/> instances against <see cref="PlaybackViewModel"/>
/// happens in MainWindow's code-behind, since a couple of the sources need a live <see cref="System.Windows.Window"/>
/// or XAML-declared control that this ViewModel has no business owning.
/// </summary>
public sealed class NoteInputSettingsViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<MidiDeviceOption> MidiDevices { get; } = new();

    public NoteInputSettingsViewModel()
    {
        var none = new MidiDeviceOption(-1, "(None)");
        MidiDevices.Add(none);
        foreach ((int index, string name) in UsbMidiKeyboardSource.GetAvailableDevices())
            MidiDevices.Add(new MidiDeviceOption(index, name));
        _selectedMidiDevice = none;

        RescanPluginsCommand = new RelayCommand(_ => RescanPlugins());
        RemoveSelectedPluginCommand = new RelayCommand(_ => RemoveSelectedPlugin(), _ => SelectedPlugin is not null);
        RescanPlugins(); // populate AvailablePlugins from the default scan path at startup
    }

    private MidiDeviceOption _selectedMidiDevice;
    public MidiDeviceOption SelectedMidiDevice
    {
        get => _selectedMidiDevice;
        set => SetField(ref _selectedMidiDevice, value);
    }

    private int _keyboardBaseOctave = 48; // C3, matches ComputerKeyboardSource's own default
    public int KeyboardBaseOctave
    {
        get => _keyboardBaseOctave;
        set => SetField(ref _keyboardBaseOctave, value);
    }

    private int _voiceCount = 16; // matches SineSynthesizer's own default
    public int VoiceCount
    {
        get => _voiceCount;
        set => SetField(ref _voiceCount, value);
    }

    private double _masterVolume = 1.0;
    public double MasterVolume
    {
        get => _masterVolume;
        set => SetField(ref _masterVolume, value);
    }

    // ---- PHASE4-VST3-NATIVE.md Milestone 7: synth engine selection ----

    public const string BuiltInEngineName = "Built-in (Sine)";
    public const string Vst3EngineName = "VST3 Plugin";

    public IReadOnlyList<string> SynthEngineOptions { get; } = new[] { BuiltInEngineName, Vst3EngineName };

    private string _synthEngine = BuiltInEngineName;
    public string SynthEngine
    {
        get => _synthEngine;
        set
        {
            SetField(ref _synthEngine, value);
            OnPropertyChanged(nameof(IsVst3EngineSelected));
        }
    }

    /// <summary>Drives the plugin picker/rescan row's <c>IsEnabled</c> and the "Show plugin
    /// editor" button - separate from <see cref="SynthEngine"/> itself so XAML doesn't need a
    /// string-equality converter.</summary>
    public bool IsVst3EngineSelected => SynthEngine == Vst3EngineName;

    public ObservableCollection<VstPluginInfo> AvailablePlugins { get; } = new();

    private VstPluginInfo? _selectedPlugin;
    public VstPluginInfo? SelectedPlugin
    {
        get => _selectedPlugin;
        set => SetField(ref _selectedPlugin, value);
    }

    // ---- Removing plugins from the picker (e.g. one that fails to load or was uninstalled) ----

    private readonly HashSet<string> _hiddenPluginPaths = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Paths the user has explicitly removed from the picker via
    /// <see cref="RemoveSelectedPluginCommand"/> - excluded from <see cref="AvailablePlugins"/> on
    /// every <see cref="RescanPluginsCommand"/> run so a plugin the user got rid of doesn't
    /// reappear the next time Rescan walks the same folder. Exposed as a snapshot rather than the
    /// live set so <see cref="MainViewModel"/> can persist it without depending on this type's
    /// internal representation.</summary>
    public IReadOnlyList<string> HiddenPluginPaths => _hiddenPluginPaths.ToList();

    /// <summary>Restores a previously-persisted hidden-plugin set - call once, before the first
    /// <see cref="RescanPluginsCommand"/> run that should honor it (<see cref="MainViewModel"/>'s
    /// startup load).</summary>
    public void SetHiddenPluginPaths(IEnumerable<string> paths)
    {
        _hiddenPluginPaths.Clear();
        foreach (string path in paths)
            _hiddenPluginPaths.Add(path);
    }

    public RelayCommand RemoveSelectedPluginCommand { get; }

    private void RemoveSelectedPlugin() => RemovePlugin(SelectedPlugin);

    /// <summary>Removes the given plugin from the picker and remembers it so future rescans skip it
    /// too - for a plugin that's broken (fails to initialize) or was uninstalled, so it doesn't keep
    /// cluttering the dropdown or getting auto-retried. Public (not just the selected-plugin-only
    /// <see cref="RemoveSelectedPluginCommand"/>) so <c>MainWindow</c>'s "failed to load" dialog can
    /// offer immediate removal, identifying the plugin explicitly rather than relying on it still
    /// being <see cref="SelectedPlugin"/> by the time the user responds - <c>MainWindow</c> clears
    /// the selection right after that dialog closes regardless of the user's answer, so there's no
    /// other point at which the plugin picker would let the user reach a "Remove" button while a
    /// known-broken plugin is still selected.</summary>
    public void RemovePlugin(VstPluginInfo? plugin)
    {
        if (plugin is null)
            return;

        _hiddenPluginPaths.Add(plugin.Path);
        AvailablePlugins.Remove(plugin);
        if (SelectedPlugin == plugin)
            SelectedPlugin = null;
    }

    private string _additionalScanPath = "";
    /// <summary>Optional extra folder to search for ".vst3" bundles, alongside
    /// <see cref="VstPluginScanner.DefaultScanPath"/> - some plugins install elsewhere. Only takes
    /// effect when <see cref="RescanPluginsCommand"/> runs, same as the default path (a plugin
    /// installed after the app started needs a rescan either way).</summary>
    public string AdditionalScanPath
    {
        get => _additionalScanPath;
        set => SetField(ref _additionalScanPath, value);
    }

    public RelayCommand RescanPluginsCommand { get; }

    private void RescanPlugins()
    {
        VstPluginInfo? previouslySelected = SelectedPlugin;

        var scanPaths = new List<string> { VstPluginScanner.DefaultScanPath };
        if (!string.IsNullOrWhiteSpace(AdditionalScanPath))
            scanPaths.Add(AdditionalScanPath);

        AvailablePlugins.Clear();
        foreach (VstPluginInfo plugin in VstPluginScanner.Scan(scanPaths))
        {
            if (!_hiddenPluginPaths.Contains(plugin.Path))
                AvailablePlugins.Add(plugin);
        }

        // Keep the same plugin selected across a rescan if it's still there (by path, since a
        // rescan creates all-new VstPluginInfo record instances even for an unchanged plugin).
        SelectedPlugin = previouslySelected is null
            ? null
            : AvailablePlugins.FirstOrDefault(p => p.Path == previouslySelected.Path);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
