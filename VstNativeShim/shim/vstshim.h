// PHASE4-VST3-NATIVE.md Milestone 4: flat extern "C" API wrapping the native VST3 hosting
// pipeline proven in Milestones 1-3, so C# can P/Invoke into it without any COM/VST3 knowledge
// on the managed side. Signatures match the phase doc's indicative API (adjusted only where the
// doc says "adjust as needed" - see PROGRESS-NATIVE.md for what changed and why).
#pragma once

extern "C"
{
    // Loads the plugin module and instantiates its first Audio Module Class (same selection
    // logic as Milestones 1-3). Returns an opaque handle, or nullptr on failure - call
    // VstShim_GetLastError() for why.
    __declspec (dllexport) void* VstShim_LoadPlugin (const wchar_t* path);

    __declspec (dllexport) void VstShim_UnloadPlugin (void* handle);

    // Runs the plugin through VST3's setup sequence (initialize/setupProcessing/activateBus/
    // setActive/setProcessing) - same sequence Milestones 2-3 proved manually. Returns 1 on
    // success, 0 on failure.
    __declspec (dllexport) int VstShim_Initialize (void* handle, double sampleRate, int blockSize);

    // Queues a Note On/Off to be applied at the start of the next internally-rendered block
    // inside VstShim_Process - see PROGRESS-NATIVE.md for the threading contract this implies.
    __declspec (dllexport) void VstShim_NoteOn (void* handle, int midiNote, int velocity);
    __declspec (dllexport) void VstShim_NoteOff (void* handle, int midiNote);
    __declspec (dllexport) void VstShim_AllNotesOff (void* handle);

    // Renders exactly numSamples interleaved float frames into the caller-provided buffer
    // (channel count fixed at whatever VstShim_Initialize's main output bus reported - query via
    // VstShim_GetOutputChannelCount). Returns numSamples on success, -1 on failure.
    __declspec (dllexport) int VstShim_Process (void* handle, float* outputBuffer, int numSamples);

    __declspec (dllexport) int VstShim_GetOutputChannelCount (void* handle);
    __declspec (dllexport) const char* VstShim_GetLastError (void* handle);

    // Milestone 6: GUI editor embedding. Returns 1/0 for success/failure throughout - query
    // VstShim_GetLastError on failure. VstShim_OpenEditor expects parentHwnd to already exist
    // (created by the C# side, e.g. a WPF HwndHost's child window) and embeds the plugin's own
    // editor view inside it, per IPlugView::attached's contract.
    __declspec (dllexport) int VstShim_HasEditor (void* handle);
    __declspec (dllexport) int VstShim_OpenEditor (void* handle, void* parentHwnd, int* outWidth, int* outHeight);
    __declspec (dllexport) void VstShim_CloseEditor (void* handle);

    // Diagnostic-only, added to verify the performEdit -> ProcessData::inputParameterChanges fix
    // without needing a real mouse drag on the plugin's own GUI (see PROGRESS-NATIVE.md). Wires
    // the controller/connection (same as VstShim_OpenEditor) but skips the view/HWND part, so no
    // parent window is needed. VstShim_DebugSetParameter calls the exact same
    // ComponentHandlerStub::performEdit a real GUI edit would call - this is not a separate,
    // easier-to-pass code path, it's the real one, just triggered programmatically.
    __declspec (dllexport) int VstShim_DebugGetParameterCount (void* handle);
    __declspec (dllexport) int VstShim_DebugGetParameterInfo (void* handle, int index, wchar_t* nameBuffer, int nameBufferSize, unsigned int* outParamId);
    __declspec (dllexport) void VstShim_DebugSetParameter (void* handle, unsigned int paramId, double normalizedValue);

    // Added while investigating the "--paramtest passes, --gui with a real knob does not" report.
    // GetBuildInfo rules out a stale DLL (item 1); CaptureState/RestoreState test IComponent's
    // setState path specifically (item 4), separate from performEdit.
    __declspec (dllexport) const char* VstShim_GetBuildInfo ();
    __declspec (dllexport) void* VstShim_DebugCaptureState (void* handle);
    __declspec (dllexport) int VstShim_DebugRestoreState (void* handle, void* stateHandle);
}
