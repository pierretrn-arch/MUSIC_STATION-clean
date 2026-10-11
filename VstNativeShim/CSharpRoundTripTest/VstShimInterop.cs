// P/Invoke declarations matching VstNativeShim/shim/vstshim.h exactly. This mirrors what
// ToneDetector.Audio/Synth/VstNativeInterop.cs will eventually look like in Milestone 7 - kept
// here in the standalone test project for now per the phase doc ("still not part of
// ToneDetector.sln yet").
using System.Runtime.InteropServices;

namespace CSharpRoundTripTest;

internal static class VstShimInterop
{
    private const string DllName = "VstShim.dll";

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    public static extern IntPtr VstShim_LoadPlugin(string path);

    [DllImport(DllName)]
    public static extern void VstShim_UnloadPlugin(IntPtr handle);

    [DllImport(DllName)]
    public static extern int VstShim_Initialize(IntPtr handle, double sampleRate, int blockSize);

    [DllImport(DllName)]
    public static extern void VstShim_NoteOn(IntPtr handle, int midiNote, int velocity);

    [DllImport(DllName)]
    public static extern void VstShim_NoteOff(IntPtr handle, int midiNote);

    [DllImport(DllName)]
    public static extern void VstShim_AllNotesOff(IntPtr handle);

    // Pointer-based rather than float[] so callers (VstShimSampleProvider.Read in particular) can
    // pass buffer+offset directly via `fixed`, matching the phase doc's "pinned array pointer -
    // zero-copy, avoid marshaling overhead on the audio thread" intent literally rather than
    // relying on default array marshaling's own (also zero-copy, but offset-0-only) pinning.
    [DllImport(DllName)]
    public static extern unsafe int VstShim_Process(IntPtr handle, float* outputBuffer, int numSamples);

    [DllImport(DllName)]
    public static extern int VstShim_GetOutputChannelCount(IntPtr handle);

    // IntPtr, not string - returning `string` directly from a DllImport had the CLR marshaler
    // attempt to free the native pointer after copying it (a real, reproducible
    // STATUS_HEAP_CORRUPTION crash was traced to exactly this - see PROGRESS-NATIVE.md), which is
    // catastrophic for a pointer into a std::string/static buffer the C++ side still owns.
    // Marshal.PtrToStringAnsi below just reads the bytes; nothing is ever freed on the C# side.
    [DllImport(DllName, EntryPoint = "VstShim_GetLastError")]
    private static extern IntPtr VstShim_GetLastError_Native(IntPtr handle);
    public static string VstShim_GetLastError(IntPtr handle) =>
        Marshal.PtrToStringAnsi(VstShim_GetLastError_Native(handle)) ?? "";

    [DllImport(DllName)]
    public static extern int VstShim_HasEditor(IntPtr handle);

    [DllImport(DllName)]
    public static extern int VstShim_OpenEditor(IntPtr handle, IntPtr parentHwnd, out int outWidth, out int outHeight);

    [DllImport(DllName)]
    public static extern void VstShim_CloseEditor(IntPtr handle);

    [DllImport(DllName, EntryPoint = "VstShim_GetBuildInfo")]
    private static extern IntPtr VstShim_GetBuildInfo_Native();
    public static string VstShim_GetBuildInfo() =>
        Marshal.PtrToStringAnsi(VstShim_GetBuildInfo_Native()) ?? "";

    [DllImport(DllName)]
    public static extern IntPtr VstShim_DebugCaptureState(IntPtr handle);

    [DllImport(DllName)]
    public static extern int VstShim_DebugRestoreState(IntPtr handle, IntPtr stateHandle);

    [DllImport(DllName)]
    public static extern int VstShim_DebugGetParameterCount(IntPtr handle);

    [DllImport(DllName, CharSet = CharSet.Unicode)]
    public static extern int VstShim_DebugGetParameterInfo(IntPtr handle, int index, System.Text.StringBuilder nameBuffer, int nameBufferSize, out uint outParamId);

    [DllImport(DllName)]
    public static extern void VstShim_DebugSetParameter(IntPtr handle, uint paramId, double normalizedValue);
}
