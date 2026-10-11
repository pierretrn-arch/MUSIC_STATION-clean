using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ToneDetector.Audio.Input;

namespace ToneDetector.App.Input;

/// <summary>
/// Standard DAW-style "keyboard as piano" input, hooked at the <see cref="Window"/> level. Lower
/// row (Z X C V B N M , . /) is one octave of white keys starting at <see cref="BaseOctaveMidiNote"/>,
/// with S D _ G H J _ L as the interleaved black keys (the classic FL Studio/Ableton-style typing-
/// keyboard layout); upper row (Q W E R T Y U I O P) is the next octave's white keys only. Page
/// Up/Down shift the base octave — deliberately not Z/X, which are already note keys.
/// Keys held with Ctrl or Alt are shortcuts, never notes. While a piano roll has keyboard focus,
/// V / D / E / S are its tool shortcuts (Select, Draw, Erase, Split) instead of notes.
/// </summary>
public sealed class ComputerKeyboardSource : INoteInputSource
{
    private const int DefaultVelocity = 100;
    private const int MinBaseMidiNote = 0;
    private const int MaxBaseMidiNote = 115; // leaves room for the +28 semitone offset (P) within 0-127

    private static readonly Dictionary<Key, int> KeyToSemitoneOffset = new()
    {
        // Lower octave: white keys + interleaved black keys.
        [Key.Z] = 0, [Key.S] = 1, [Key.X] = 2, [Key.D] = 3, [Key.C] = 4,
        [Key.V] = 5, [Key.G] = 6, [Key.B] = 7, [Key.H] = 8, [Key.N] = 9,
        [Key.J] = 10, [Key.M] = 11, [Key.OemComma] = 12, [Key.L] = 13,
        [Key.OemPeriod] = 14, [Key.OemQuestion] = 16,

        // Upper octave: white keys only.
        [Key.Q] = 12, [Key.W] = 14, [Key.E] = 16, [Key.R] = 17, [Key.T] = 19,
        [Key.Y] = 21, [Key.U] = 23, [Key.I] = 24, [Key.O] = 26, [Key.P] = 28,
    };

    /// <summary>The piano roll's single-letter tool shortcuts.</summary>
    internal static readonly IReadOnlySet<Key> RollToolKeys = new HashSet<Key> { Key.V, Key.D, Key.E, Key.S };

    private readonly Window _window;
    private readonly HashSet<Key> _heldKeys = new();
    private bool _isActive;

    public event Action<int, int>? NoteOn;
    public event Action<int>? NoteOff;

    public int BaseOctaveMidiNote { get; set; } = 48; // C3

    public ComputerKeyboardSource(Window window)
    {
        _window = window;
    }

    public void Start()
    {
        if (_isActive)
            return;
        _window.PreviewKeyDown += OnPreviewKeyDown;
        _window.PreviewKeyUp += OnPreviewKeyUp;
        _isActive = true;
    }

    public void Stop()
    {
        if (_isActive)
        {
            _window.PreviewKeyDown -= OnPreviewKeyDown;
            _window.PreviewKeyUp -= OnPreviewKeyUp;
            _isActive = false;
        }

        // Release anything still held so a note doesn't get stuck sounding if the feature is
        // toggled off mid-chord. Unconditional (not gated on _isActive above) so it's always
        // correct defensive cleanup, not dependent on Start() having been called first.
        foreach (Key key in _heldKeys.ToList())
            RaiseNoteOff(key);
        _heldKeys.Clear();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleKeyDown(e.Key, IsFocusInTextInput(), Keyboard.Modifiers, Keyboard.FocusedElement is Controls.PianoRollControl))
            e.Handled = true;
    }

    /// <summary>As <see cref="HandleKeyDown(Key, bool)"/>, but Ctrl/Alt combinations are left to the
    /// shortcuts, and so are the roll's tool letters while a piano roll has focus.</summary>
    internal bool HandleKeyDown(Key key, bool isFocusInTextInput, ModifierKeys modifiers, bool isPianoRollFocused)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) != 0)
            return false;
        if (isPianoRollFocused && RollToolKeys.Contains(key))
            return false;
        return HandleKeyDown(key, isFocusInTextInput);
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (HandleKeyUp(e.Key))
            e.Handled = true;
    }

    /// <summary>Core key-down logic, taking the focus check as a parameter so it's directly
    /// unit-testable without a live <see cref="Window"/> or real keyboard focus. Returns whether
    /// the key was consumed (a note key or an octave-shift key).</summary>
    internal bool HandleKeyDown(Key key, bool isFocusInTextInput)
    {
        if (isFocusInTextInput)
            return false; // avoid triggering notes while typing in a Settings TextBox, etc.

        if (key == Key.PageUp)
        {
            BaseOctaveMidiNote = Math.Min(MaxBaseMidiNote, BaseOctaveMidiNote + 12);
            return true;
        }
        if (key == Key.PageDown)
        {
            BaseOctaveMidiNote = Math.Max(MinBaseMidiNote, BaseOctaveMidiNote - 12);
            return true;
        }

        if (!KeyToSemitoneOffset.TryGetValue(key, out int offset))
            return false;
        if (!_heldKeys.Add(key))
            return true; // OS key-repeat: already sounding, don't re-trigger NoteOn

        NoteOn?.Invoke(BaseOctaveMidiNote + offset, DefaultVelocity);
        return true;
    }

    internal bool HandleKeyUp(Key key)
    {
        if (!_heldKeys.Remove(key))
            return false;

        RaiseNoteOff(key);
        return true;
    }

    private void RaiseNoteOff(Key key)
    {
        if (KeyToSemitoneOffset.TryGetValue(key, out int offset))
            NoteOff?.Invoke(BaseOctaveMidiNote + offset);
    }

    private static bool IsFocusInTextInput() => Keyboard.FocusedElement is TextBox;
}
