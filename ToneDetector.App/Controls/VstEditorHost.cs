using System.Runtime.InteropServices;
using System.Windows.Interop;
using ToneDetector.Audio.Synth;

namespace ToneDetector.App.Controls;

/// <summary>
/// WPF <see cref="HwndHost"/> subclass that embeds a <see cref="VstHostSynth"/>'s native editor
/// view. Creates a plain native child window using Windows' built-in "STATIC" window class
/// (always pre-registered, no <c>RegisterClassEx</c> needed for a plain container) and hands it to
/// <see cref="VstHostSynth.OpenEditor"/> as the parent the plugin's own view attaches into - proven
/// out as <c>VstNativeShim/CSharpRoundTripTest/VstEditorHost.cs</c> in PHASE4-VST3-NATIVE.md
/// Milestone 6; this is that same approach against the real <see cref="VstHostSynth"/> API.
/// </summary>
public sealed class VstEditorHost : HwndHost
{
    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        int dwExStyle, string lpClassName, string? lpWindowName, int dwStyle,
        int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hwnd);

    private readonly VstHostSynth _synth;
    private bool _editorOpened;

    public int EditorWidth { get; private set; }
    public int EditorHeight { get; private set; }

    public VstEditorHost(VstHostSynth synth)
    {
        _synth = synth;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        IntPtr containerHwnd = CreateWindowEx(
            0, "STATIC", null, WS_CHILD | WS_VISIBLE,
            0, 0, 400, 300,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (containerHwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx failed for the VST3 editor container window.");

        if (!_synth.OpenEditor(containerHwnd, out int width, out int height))
        {
            string error = _synth.GetLastError();
            DestroyWindow(containerHwnd);
            throw new InvalidOperationException($"Failed to open the plugin's editor view: {error}");
        }

        _editorOpened = true;
        EditorWidth = width;
        EditorHeight = height;

        return new HandleRef(this, containerHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_editorOpened)
        {
            _synth.CloseEditor();
            _editorOpened = false;
        }
        DestroyWindow(hwnd.Handle);
    }
}
