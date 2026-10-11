using System.Runtime.InteropServices;

namespace ToneDetector.Audio.Synth;

/// <summary>
/// P/Invoke declarations for VstNativeShim/shim/vstshim.h's flat C API (PHASE4-VST3-NATIVE.md
/// Milestones 1-6), matching the native shim exactly - "clean and minimal" the way <see cref="ISynth"/>
/// is, since only <see cref="VstHostSynth"/> should ever call these directly.
/// </summary>
internal static class VstNativeInterop
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

    // Pointer-based (not float[]) so VstHostSynth.Read can pass buffer+offset directly via
    // `fixed`, matching the phase doc's "pinned pointer, zero-copy" intent on the audio thread.
    [DllImport(DllName)]
    public static extern unsafe int VstShim_Process(IntPtr handle, float* outputBuffer, int numSamples);

    [DllImport(DllName)]
    public static extern int VstShim_GetOutputChannelCount(IntPtr handle);

    // IntPtr, not string - a plain `string`-returning DllImport makes the CLR marshaler attempt
    // to free the native pointer, which corrupts the heap when (as here) that memory is actually
    // owned by a C++ std::string. Traced to a real STATUS_HEAP_CORRUPTION crash during the native
    // shim's own development - see VstNativeShim/PROGRESS-NATIVE.md. Marshal.PtrToStringAnsi only
    // reads the bytes and frees nothing.
    [DllImport(DllName, EntryPoint = "VstShim_GetLastError")]
    private static extern IntPtr VstShim_GetLastError_Native(IntPtr handle);
    public static string GetLastError(IntPtr handle) => Marshal.PtrToStringAnsi(VstShim_GetLastError_Native(handle)) ?? "";

    [DllImport(DllName)]
    public static extern int VstShim_HasEditor(IntPtr handle);

    [DllImport(DllName)]
    public static extern int VstShim_OpenEditor(IntPtr handle, IntPtr parentHwnd, out int outWidth, out int outHeight);

    [DllImport(DllName)]
    public static extern void VstShim_CloseEditor(IntPtr handle);
}
