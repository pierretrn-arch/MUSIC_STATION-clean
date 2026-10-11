// PHASE4-VST3-NATIVE.md Milestone 6: WPF HwndHost subclass that creates a plain native child
// window (Windows' built-in "STATIC" window class - always pre-registered, no RegisterClassEx
// needed) and hands it to VstShim_OpenEditor as the parent the plugin's own IPlugView attaches
// into. This mirrors what ToneDetector.App/Controls/VstEditorHost.cs would need to look like in
// Milestone 7 if this milestone succeeds.
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CSharpRoundTripTest;

internal sealed class VstEditorHost : HwndHost
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

    private readonly IntPtr _pluginHandle;
    private IntPtr _containerHwnd;
    private bool _editorOpened;

    public int EditorWidth { get; private set; }
    public int EditorHeight { get; private set; }

    public VstEditorHost(IntPtr pluginHandle)
    {
        _pluginHandle = pluginHandle;
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _containerHwnd = CreateWindowEx(
            0, "STATIC", null, WS_CHILD | WS_VISIBLE,
            0, 0, 400, 300,
            hwndParent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_containerHwnd == IntPtr.Zero)
            throw new InvalidOperationException("CreateWindowEx failed for the editor container window.");

        if (VstShimInterop.VstShim_OpenEditor(_pluginHandle, _containerHwnd, out int width, out int height) == 0)
        {
            string error = VstShimInterop.VstShim_GetLastError(_pluginHandle);
            throw new InvalidOperationException($"VstShim_OpenEditor failed: {error}");
        }

        _editorOpened = true;
        EditorWidth = width;
        EditorHeight = height;

        return new HandleRef(this, _containerHwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (_editorOpened)
        {
            VstShimInterop.VstShim_CloseEditor(_pluginHandle);
            _editorOpened = false;
        }
        DestroyWindow(hwnd.Handle);
    }
}
