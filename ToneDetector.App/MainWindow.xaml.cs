using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using System.Windows.Threading;
using ToneDetector.App.Controls;
using ToneDetector.App.Input;
using ToneDetector.App.ViewModels;
using ToneDetector.Audio.Input;
using ToneDetector.Audio.Synth;
using ToneDetector.Core.Pitch;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private readonly ComputerKeyboardSource _computerKeyboardSource;
    private UsbMidiKeyboardSource? _midiSource;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        Closing += (_, e) =>
        {
            // There's no project save yet: closing loses the tracks.
            if (_viewModel.HasUnsavedTracks && !Views.ConfirmDialog.Ask(this, "Close Music Station? Your tracks aren't saved."))
                e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            _viewModel.SaveSettings();
            _viewModel.Dispose();
        };

        _computerKeyboardSource = new ComputerKeyboardSource(this) { BaseOctaveMidiNote = _viewModel.NoteInput.KeyboardBaseOctave };
        _viewModel.Playback.AttachInputSource(_computerKeyboardSource);
        _viewModel.Playback.AttachInputSource(LiveTunerPage.Roll);
        _viewModel.Playback.AttachInputSource(FileAnalysisPage.Roll);
        _viewModel.Playback.Volume = (float)_viewModel.NoteInput.MasterVolume;

        _viewModel.NoteInput.PropertyChanged += NoteInput_PropertyChanged;

        Deactivated += (_, _) => _mouseOpenedDialog = InputManager.Current.MostRecentInputDevice is MouseDevice;
        Activated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, HideRestoredFocusRing);
    }

    private bool _mouseOpenedDialog;

    /// <summary>Focus rings are for keyboard focus only. When a dialog opened with the mouse
    /// (Load file, the MIDI parts picker, Export) closes, WPF gives focus back to the button that
    /// opened it and draws its ring; re-focusing it while the mouse was the last input takes the
    /// ring away without moving focus.</summary>
    private void HideRestoredFocusRing()
    {
        if (!_mouseOpenedDialog || Keyboard.FocusedElement is not UIElement focused || focused is TextBox)
            return;
        _mouseOpenedDialog = false;
        Keyboard.ClearFocus();
        focused.Focus();
    }

    /// <summary>Keyboard input plays through the selected track of the tab you're on (the Live
    /// Tuner's outside the two editing tabs).</summary>
    private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabControl))
            return;
        _viewModel.Playback.InputRoll = ReferenceEquals(MainTabControl.SelectedItem, FileAnalysisTabItem)
            ? _viewModel.FileAnalysis.Roll
            : _viewModel.PianoRoll;
    }

    /// <summary>Home "Open piano roll": the piano roll is the centre of the Live Tuner tab.</summary>
    private void HomeView_OpenPianoRollRequested(object? sender, EventArgs e) =>
        MainTabControl.SelectedItem = LiveTunerTabItem;

    private void NoteInput_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NoteInputSettingsViewModel.SelectedMidiDevice):
                if (_midiSource is not null)
                {
                    _viewModel.Playback.DetachInputSource(_midiSource);
                    _midiSource = null;
                }
                if (_viewModel.NoteInput.SelectedMidiDevice.Index >= 0)
                {
                    _midiSource = new UsbMidiKeyboardSource(_viewModel.NoteInput.SelectedMidiDevice.Index);
                    _viewModel.Playback.AttachInputSource(_midiSource);
                }
                break;

            case nameof(NoteInputSettingsViewModel.KeyboardBaseOctave):
                _computerKeyboardSource.BaseOctaveMidiNote = _viewModel.NoteInput.KeyboardBaseOctave;
                break;

            case nameof(NoteInputSettingsViewModel.VoiceCount):
                if (_viewModel.NoteInput.VoiceCount >= 1)
                    _viewModel.Playback.SetSynthVoiceCount(_viewModel.NoteInput.VoiceCount);
                break;

            case nameof(NoteInputSettingsViewModel.MasterVolume):
                _viewModel.Playback.Volume = (float)_viewModel.NoteInput.MasterVolume;
                break;

            case nameof(NoteInputSettingsViewModel.SynthEngine):
            case nameof(NoteInputSettingsViewModel.SelectedPlugin):
                ApplySynthEngineSelection();
                break;
        }
    }

    /// <summary>Reconciles the live synth with whatever the Settings tab's "Synth engine"/
    /// "VST3 plugin" controls currently say - called whenever either changes. Constructing
    /// <see cref="VstHostSynth"/> can fail (bad/incompatible plugin file), in which case
    /// <see cref="PlaybackViewModel.SwitchToVst3Plugin"/> throws before touching the previous
    /// synth (see its own doc comment) - reported here and reverted to Built-in rather than left
    /// on a half-switched state.</summary>
    private void ApplySynthEngineSelection()
    {
        NoteInputSettingsViewModel noteInput = _viewModel.NoteInput;

        if (noteInput.SynthEngine == NoteInputSettingsViewModel.BuiltInEngineName)
        {
            _viewModel.Playback.SwitchToBuiltInSynth();
            return;
        }

        if (noteInput.SelectedPlugin is null)
            return; // VST3 selected but no plugin chosen yet - leave whatever's currently active alone

        VstPluginInfo failedPlugin = noteInput.SelectedPlugin;
        try
        {
            _viewModel.Playback.SwitchToVst3Plugin(failedPlugin.Path);
        }
        catch (Exception ex)
        {
            MessageBoxResult remove = MessageBox.Show(this,
                $"Failed to load '{failedPlugin.Name}':\n{ex.Message}\n\nRemove it from the VST3 plugin list so it isn't tried again?",
                "VST3 Plugin", MessageBoxButton.YesNo, MessageBoxImage.Error);
            if (remove == MessageBoxResult.Yes)
                noteInput.RemovePlugin(failedPlugin);

            // Clear the selection *before* reverting the engine, not just the engine itself (even
            // when the user answered No above and the plugin is still in the list): reverting
            // SynthEngine alone leaves SelectedPlugin pointing at the same plugin that just failed,
            // so the next time the user flips "Synth engine" back to "VST3 Plugin" this same method
            // re-fires, retries the identical broken plugin, fails again, and reverts again - from
            // the user's perspective the VST3 plugin picker (disabled via IsVst3EngineSelected
            // whenever the engine isn't "VST3 Plugin") never stays enabled long enough to pick a
            // different plugin instead.
            noteInput.SelectedPlugin = null;
            noteInput.SynthEngine = NoteInputSettingsViewModel.BuiltInEngineName;
        }
    }

    /// <summary>Opens the current VST3 plugin's own editor in a separate floating window - a
    /// second window rather than embedding it in the main layout, since the editor's natural size
    /// varies wildly by plugin (Surge XT's is over 1100px wide) and a floating window sidesteps
    /// needing to redesign the Live Tuner/Settings layout around that.</summary>
    private void SettingsView_ShowPluginEditorRequested(object? sender, EventArgs e)
    {
        if (_viewModel.Playback.CurrentSynth is not VstHostSynth vstSynth)
        {
            MessageBox.Show(this, "Select a VST3 plugin first.", "VST3 Plugin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!vstSynth.HasEditor)
        {
            MessageBox.Show(this, $"'{System.IO.Path.GetFileNameWithoutExtension(vstSynth.PluginPath)}' has no editor GUI.",
                "VST3 Plugin", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var editorHost = new VstEditorHost(vstSynth);
        var editorWindow = new Window
        {
            Title = $"{System.IO.Path.GetFileNameWithoutExtension(vstSynth.PluginPath)} - Editor",
            Width = 900,
            Height = 700,
            Content = editorHost,
            Owner = this,
        };
        editorWindow.Show();
    }
}
