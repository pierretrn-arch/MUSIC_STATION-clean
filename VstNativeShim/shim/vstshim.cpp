// PHASE4-VST3-NATIVE.md Milestone 4 implementation. Wraps the exact sequence proven manually in
// Milestones 1-3 (load module -> find Audio Module Class -> createInstance<IComponent> ->
// initialize -> setupProcessing -> discover main output/event busses -> activate -> setActive ->
// setProcessing -> process) behind the flat C API declared in vstshim.h.

#include "vstshim.h"

#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/hosting/connectionproxy.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/common/memorystream.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/gui/iplugview.h"

#include <windows.h>
#include <mutex>
#include <set>
#include <vector>
#include <string>
#include <algorithm>
#include <iostream>
#include <sstream>

using namespace VST3::Hosting;
using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

// Load-time failures (bad path, no Audio Module Class, createInstance failure) happen before a
// handle exists for VstShim_GetLastError(handle) to read from, so they land here instead;
// VstShim_GetLastError(nullptr) reads this. Everything after a successful load stores its error
// on the instance itself.
thread_local std::string g_lastLoadError;

// Diagnostic logging (added investigating the "--paramtest passes, real mouse doesn't" report)
// writes to std::cerr from multiple threads with no coordination otherwise - NoteOn/performEdit
// from whatever thread drives input, Process from NAudio's own real-time render thread. Concurrent
// unsynchronized << chains on the same stream is exactly the kind of thing that can corrupt
// iostream's internal state (this was in fact the cause of a real STATUS_HEAP_CORRUPTION crash
// while first testing this logging - see PROGRESS-NATIVE.md). A dedicated mutex just for logging
// keeps it out of the way of eventMutex, which guards actual program state, not diagnostics.
std::mutex g_logMutex;
#define VSTSHIM_LOG(expr) do { std::lock_guard<std::mutex> _logLock (g_logMutex); std::cerr << expr; } while (0)

// Milestone 6: minimal IPlugFrame/IComponentHandler stubs for GUI embedding, following the exact
// pattern used by the SDK's own public.sdk/samples/vst-hosting/editorhost sample - fixed
// addRef/release (host-owned, outlives any plugin release() call) and a hand-rolled
// queryInterface via FUnknownPrivate::iidEqual rather than the DECLARE_FUNKNOWN_METHODS macro,
// since these are plain members of ShimInstance rather than separately heap-allocated/refcounted
// objects.
class EditorFrame : public IPlugFrame
{
public:
    tresult PLUGIN_API resizeView (IPlugView* view, ViewRect* newSize) override
    {
        // Accept whatever the plugin asks for - onSize() must be called or the plugin may
        // consider the resize request unanswered. Not propagated back to the C# host window in
        // this milestone (documented limitation - see PROGRESS-NATIVE.md).
        if (view && newSize)
            view->onSize (newSize);
        return kResultOk;
    }

    tresult PLUGIN_API queryInterface (const TUID _iid, void** obj) override
    {
        if (FUnknownPrivate::iidEqual (_iid, IPlugFrame::iid) || FUnknownPrivate::iidEqual (_iid, FUnknown::iid))
        {
            *obj = this;
            addRef ();
            return kResultTrue;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef () override { return 1000; }
    uint32 PLUGIN_API release () override { return 1000; }
};

struct ShimInstance; // forward declaration - performEdit needs the full type, defined below

// Real IComponentHandler, not a no-op stub: when the plugin's own editor calls performEdit (e.g.
// the user turns a knob), a host MUST turn that into (a) an updated cached value on the
// controller and (b) a parameter change queued for the NEXT process() call - otherwise the
// change is purely cosmetic (the controller/GUI updates itself internally regardless of what the
// host does, which is exactly why Milestone 6 first looked like it worked - the GUI visibly
// updated - while the actual audio never changed). See PROGRESS-NATIVE.md for the full
// investigation; owner is set once in VstShim_OpenEditor.
class ComponentHandlerStub : public IComponentHandler
{
public:
    ShimInstance* owner = nullptr;

    tresult PLUGIN_API beginEdit (ParamID) override { return kResultOk; }
    tresult PLUGIN_API performEdit (ParamID id, ParamValue valueNormalized) override;
    tresult PLUGIN_API endEdit (ParamID) override { return kResultOk; }
    tresult PLUGIN_API restartComponent (int32) override { return kResultOk; }

    tresult PLUGIN_API queryInterface (const TUID _iid, void** obj) override
    {
        if (FUnknownPrivate::iidEqual (_iid, IComponentHandler::iid) || FUnknownPrivate::iidEqual (_iid, FUnknown::iid))
        {
            *obj = this;
            addRef ();
            return kResultTrue;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef () override { return 1000; }
    uint32 PLUGIN_API release () override { return 1000; }
};

struct ShimInstance
{
    Module::Ptr module;
    IPtr<IComponent> component;
    IPtr<IAudioProcessor> processor;
    HostApplication hostContext;

    // Milestone 6 editor state - all null/false until VstShim_OpenEditor succeeds.
    IPtr<IEditController> controller;
    IPtr<IPlugView> plugView;
    IPtr<ConnectionProxy> componentConnectionProxy;
    IPtr<ConnectionProxy> controllerConnectionProxy;
    EditorFrame editorFrame;
    ComponentHandlerStub componentHandlerStub;
    bool editorOpen = false;

    // GUI parameter edits (performEdit, called on whatever thread the plugin's own view lives
    // on - the WPF UI thread in this shim's usage) are handed off to VstShim_Process's caller
    // thread via this SDK-provided lock-free single-writer/single-reader ring buffer, then
    // drained into inputParamChanges and attached to ProcessData once per block. 128 is a
    // generous cap on distinct parameter IDs edited within a single block, not a limit on total
    // edits over time.
    ParameterChangeTransfer paramTransfer {128};
    ParameterChanges inputParamChanges {128};

    int32 mainOutputBusIndex = -1;
    int32 eventInputBusIndex = -1;
    int32 numInputBusses = 0;
    int32 numOutputBusses = 0;
    int outChannels = 0;

    int32 blockSize = 512;
    bool initialized = false;

    HostProcessData processData;
    ProcessContext context {};

    // NoteOn/NoteOff/AllNotesOff (expected to be called from a UI/input thread) append to
    // pendingEvents; VstShim_Process (expected to be called from an audio thread) drains it once
    // per internally-rendered block. See PROGRESS-NATIVE.md for why this lock exists at all -
    // it's not needed if every call happens from one thread, but the real ToneDetector.App would
    // call NoteOn/Off from a different thread than Process, so this shim is built to be correct
    // for that case from the start rather than retrofitted later.
    std::mutex eventMutex;
    std::vector<Event> pendingEvents;
    std::set<int> heldNotes;

    std::vector<float> pendingOutput; // interleaved leftover from the last internally-rendered block
    size_t pendingReadFrame = 0;
    int64 processCallCount = 0; // diagnostic-only, for throttled logging

    std::string lastError;
};

tresult PLUGIN_API ComponentHandlerStub::performEdit (ParamID id, ParamValue valueNormalized)
{
    // Unconditional, un-throttled - performEdit only fires on an actual GUI edit, never in a
    // tight loop, so logging every call is safe and this is exactly the log line needed to prove
    // (or disprove) that a real mouse drag reaches this stub at all.
    VSTSHIM_LOG ("[VstShim] performEdit: owner=" << static_cast<void*> (owner)
              << " paramId=" << id << " value=" << valueNormalized << "\n");

    if (!owner)
    {
        VSTSHIM_LOG ("[VstShim] performEdit: owner is NULL - this edit is being silently dropped right here.\n");
        return kResultOk;
    }

    // Keep the controller's own cached value in sync - mostly a formality here, since the
    // controller already updated itself internally before calling performEdit (that's why the
    // GUI visibly changes regardless of anything the host does), but it's what a real host is
    // expected to do and matters for hosts that also drive automation/undo from this callback.
    if (owner->controller)
        owner->controller->setParamNormalized (id, valueNormalized);

    // The part that was actually missing: hand the change to the audio-processing side. This
    // queue is drained into ProcessData::inputParameterChanges once per block in VstShim_Process,
    // which is the only way IAudioProcessor::process ever finds out a parameter changed.
    owner->paramTransfer.addChange (id, valueNormalized, 0);
    return kResultOk;
}

// Shared by VstShim_OpenEditor and the VstShim_Debug* diagnostics - creates and wires the
// controller (class lookup, instantiate, initialize, componentHandler, ConnectionProxy) without
// touching the view/HWND side, so the diagnostics don't need a real parent window. No-ops
// (returns true immediately) if a controller is already connected.
bool EnsureController (ShimInstance& instance)
{
    if (instance.controller)
        return true;

    TUID controllerCid {};
    if (instance.component->getControllerClassId (controllerCid) != kResultTrue)
    {
        instance.lastError = "getControllerClassId failed - plugin has no separate controller class.";
        return false;
    }

    const PluginFactory& factory = instance.module->getFactory ();
    instance.controller = factory.createInstance<IEditController> (VST3::UID (controllerCid));
    if (!instance.controller)
    {
        instance.lastError = "createInstance<IEditController> returned null.";
        return false;
    }

    if (instance.controller->initialize (&instance.hostContext) != kResultOk)
    {
        instance.lastError = "IEditController::initialize failed.";
        instance.controller = nullptr;
        return false;
    }

    instance.componentHandlerStub.owner = &instance;
    instance.controller->setComponentHandler (&instance.componentHandlerStub);

    IConnectionPoint* compICP = nullptr;
    IConnectionPoint* ctrlICP = nullptr;
    instance.component->queryInterface (IConnectionPoint::iid, reinterpret_cast<void**> (&compICP));
    instance.controller->queryInterface (IConnectionPoint::iid, reinterpret_cast<void**> (&ctrlICP));
    if (compICP && ctrlICP)
    {
        instance.componentConnectionProxy = owned (new ConnectionProxy (compICP));
        instance.controllerConnectionProxy = owned (new ConnectionProxy (ctrlICP));
        instance.componentConnectionProxy->connect (ctrlICP);
        instance.controllerConnectionProxy->connect (compICP);
    }
    if (compICP)
        compICP->release ();
    if (ctrlICP)
        ctrlICP->release ();

    return true;
}

std::string WideToUtf8 (const wchar_t* wide)
{
    int len = WideCharToMultiByte (CP_UTF8, 0, wide, -1, nullptr, 0, nullptr, nullptr);
    if (len <= 0)
        return {};
    std::string result (static_cast<size_t> (len - 1), '\0');
    WideCharToMultiByte (CP_UTF8, 0, wide, -1, result.data (), len, nullptr, nullptr);
    return result;
}

void QueueNoteEvent (ShimInstance& instance, uint16 type, int midiNote, float velocity)
{
    Event e {};
    e.busIndex = instance.eventInputBusIndex >= 0 ? instance.eventInputBusIndex : 0;
    e.sampleOffset = 0;
    e.type = type;
    if (type == Event::kNoteOnEvent)
    {
        e.noteOn.channel = 0;
        e.noteOn.pitch = static_cast<int16> (midiNote);
        e.noteOn.tuning = 0.0f;
        e.noteOn.velocity = velocity;
        e.noteOn.length = 0;
        e.noteOn.noteId = -1; // per spec, -1 means "not tracked" - this shim doesn't correlate on/off pairs by id
    }
    else
    {
        e.noteOff.channel = 0;
        e.noteOff.pitch = static_cast<int16> (midiNote);
        e.noteOff.velocity = 0.0f;
        e.noteOff.noteId = -1;
        e.noteOff.tuning = 0.0f;
    }

    std::lock_guard<std::mutex> lock (instance.eventMutex);
    instance.pendingEvents.push_back (e);
}

} // namespace

void* VstShim_LoadPlugin (const wchar_t* path)
{
    std::string utf8Path = WideToUtf8 (path);

    std::string error;
    Module::Ptr module = Module::create (utf8Path, error);
    if (!module)
    {
        g_lastLoadError = "Module::create failed: " + error;
        return nullptr;
    }

    const PluginFactory& factory = module->getFactory ();
    PluginFactory::ClassInfos classInfos = factory.classInfos ();
    const ClassInfo* audioEffectClass = nullptr;
    for (const ClassInfo& info : classInfos)
    {
        if (info.category () == kVstAudioEffectClass)
        {
            audioEffectClass = &info;
            break;
        }
    }
    if (!audioEffectClass)
    {
        g_lastLoadError = "No '" + std::string (kVstAudioEffectClass) + "' class found in plugin.";
        return nullptr;
    }

    IPtr<IComponent> component = factory.createInstance<IComponent> (audioEffectClass->ID ());
    if (!component)
    {
        g_lastLoadError = "createInstance<IComponent> returned null.";
        return nullptr;
    }

    auto* instance = new ShimInstance ();
    instance->module = module;
    instance->component = component;
    return instance;
}

void VstShim_UnloadPlugin (void* handle)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance)
        return;

    // Must happen before anything below, and definitely before `delete instance` - the WPF side
    // (VstEditorHost.DestroyWindowCore) also calls VstShim_CloseEditor, but only as part of its
    // own HwndHost teardown, which can run *after* a caller has already called UnloadPlugin (this
    // is exactly what caused an AccessViolationException calling CloseEditor on an
    // already-freed instance - see PROGRESS-NATIVE.md). Closing it here defensively means the
    // instance is always still valid whenever CloseEditor's own logic runs, regardless of what
    // order the C# side calls things in.
    if (instance->editorOpen)
        VstShim_CloseEditor (handle);

    if (instance->initialized)
    {
        if (instance->processor)
            instance->processor->setProcessing (false);
        if (instance->eventInputBusIndex >= 0)
            instance->component->activateBus (kEvent, kInput, instance->eventInputBusIndex, false);
        for (int32 i = 0; i < instance->numOutputBusses; i++)
            instance->component->activateBus (kAudio, kOutput, i, false);
        for (int32 i = 0; i < instance->numInputBusses; i++)
            instance->component->activateBus (kAudio, kInput, i, false);
        instance->component->setActive (false);
    }
    if (instance->component)
        instance->component->terminate ();

    delete instance; // drops the IPtrs and the Module::Ptr, which unloads the native module
}

int VstShim_Initialize (void* handle, double sampleRate, int blockSize)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->component || blockSize <= 0)
        return 0;

    if (instance->component->initialize (&instance->hostContext) != kResultOk)
    {
        instance->lastError = "IComponent::initialize failed.";
        return 0;
    }

    IAudioProcessor* rawProcessor = nullptr;
    if (instance->component->queryInterface (IAudioProcessor::iid, reinterpret_cast<void**> (&rawProcessor)) != kResultOk || !rawProcessor)
    {
        instance->lastError = "Component does not implement IAudioProcessor.";
        return 0;
    }
    instance->processor = owned (rawProcessor);

    if (instance->processor->canProcessSampleSize (kSample32) != kResultOk)
    {
        instance->lastError = "Plugin does not support 32-bit float sample processing.";
        return 0;
    }

    ProcessSetup setup {};
    setup.processMode = kRealtime;
    setup.symbolicSampleSize = kSample32;
    setup.maxSamplesPerBlock = blockSize;
    setup.sampleRate = sampleRate;
    if (instance->processor->setupProcessing (setup) != kResultOk)
    {
        instance->lastError = "setupProcessing failed.";
        return 0;
    }

    instance->blockSize = blockSize;
    instance->numInputBusses = instance->component->getBusCount (kAudio, kInput);
    instance->numOutputBusses = instance->component->getBusCount (kAudio, kOutput);

    // Milestone 3 lesson: don't assume bus 0 is main - more than one bus can report kMain, so
    // take the first kMain match (which the actual plugins tested so far all happen to expose as
    // bus 0, but this checks rather than assumes).
    for (int32 i = 0; i < instance->numOutputBusses; i++)
    {
        BusInfo busInfo {};
        if (instance->component->getBusInfo (kAudio, kOutput, i, busInfo) == kResultOk && busInfo.busType == kMain)
        {
            instance->mainOutputBusIndex = i;
            break;
        }
    }
    if (instance->mainOutputBusIndex < 0)
    {
        instance->lastError = "No output bus reported busType == kMain.";
        return 0;
    }

    int32 numEventInputBusses = instance->component->getBusCount (kEvent, kInput);
    if (numEventInputBusses > 0)
        instance->eventInputBusIndex = 0;
    // No event bus at all is tolerated here (NoteOn/Off would just have nowhere to route and be
    // silently ineffective) rather than treated as a load failure - an effects-only plugin might
    // legitimately have none, and Milestone 4 only targets instruments so this shouldn't trigger
    // in practice, but failing initialization entirely over it felt like the wrong tradeoff.

    for (int32 i = 0; i < instance->numInputBusses; i++)
        instance->component->activateBus (kAudio, kInput, i, true);
    for (int32 i = 0; i < instance->numOutputBusses; i++)
        instance->component->activateBus (kAudio, kOutput, i, true);
    if (instance->eventInputBusIndex >= 0)
        instance->component->activateBus (kEvent, kInput, instance->eventInputBusIndex, true);

    if (instance->component->setActive (true) != kResultOk)
    {
        instance->lastError = "setActive(true) failed.";
        return 0;
    }

    if (instance->processor->setProcessing (true) != kResultOk)
    {
        instance->lastError = "setProcessing(true) failed.";
        return 0;
    }

    if (!instance->processData.prepare (*instance->component, blockSize, kSample32))
    {
        instance->lastError = "HostProcessData::prepare failed.";
        return 0;
    }
    instance->processData.numSamples = blockSize;
    instance->processData.symbolicSampleSize = kSample32;
    instance->processData.processMode = kRealtime;

    instance->context = ProcessContext {};
    instance->context.state = ProcessContext::kTempoValid | ProcessContext::kTimeSigValid;
    instance->context.sampleRate = sampleRate;
    instance->context.tempo = 120.0;
    instance->context.timeSigNumerator = 4;
    instance->context.timeSigDenominator = 4;
    instance->processData.processContext = &instance->context;

    instance->outChannels = instance->processData.outputs[instance->mainOutputBusIndex].numChannels;
    if (instance->outChannels <= 0)
    {
        instance->lastError = "Main output bus reports 0 channels.";
        return 0;
    }

    instance->initialized = true;
    return 1;
}

void VstShim_NoteOn (void* handle, int midiNote, int velocity)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    VSTSHIM_LOG ("[VstShim] NoteOn: instance=" << static_cast<void*> (instance)
              << " midiNote=" << midiNote << " initialized=" << (instance ? instance->initialized : false) << "\n");
    if (!instance || !instance->initialized)
        return;
    float normalizedVelocity = std::clamp (velocity, 0, 127) / 127.0f;
    QueueNoteEvent (*instance, Event::kNoteOnEvent, midiNote, normalizedVelocity);

    std::lock_guard<std::mutex> lock (instance->eventMutex);
    instance->heldNotes.insert (midiNote);
}

void VstShim_NoteOff (void* handle, int midiNote)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->initialized)
        return;
    QueueNoteEvent (*instance, Event::kNoteOffEvent, midiNote, 0.0f);

    std::lock_guard<std::mutex> lock (instance->eventMutex);
    instance->heldNotes.erase (midiNote);
}

void VstShim_AllNotesOff (void* handle)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->initialized)
        return;

    std::vector<int> notesToRelease;
    {
        std::lock_guard<std::mutex> lock (instance->eventMutex);
        notesToRelease.assign (instance->heldNotes.begin (), instance->heldNotes.end ());
        instance->heldNotes.clear ();
    }
    for (int note : notesToRelease)
        QueueNoteEvent (*instance, Event::kNoteOffEvent, note, 0.0f);
}

int VstShim_Process (void* handle, float* outputBuffer, int numSamples)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->initialized || !outputBuffer || numSamples <= 0)
        return -1;

    instance->processCallCount++;
    // Throttled (called every ~10ms; logging every call would flood the console) - still enough
    // to correlate this instance pointer against NoteOn/performEdit's un-throttled logs.
    if (instance->processCallCount % 200 == 1)
        VSTSHIM_LOG ("[VstShim] Process: instance=" << static_cast<void*> (instance)
                  << " callCount=" << instance->processCallCount << "\n");

    int channels = instance->outChannels;
    int framesWritten = 0;

    while (framesWritten < numSamples)
    {
        size_t leftoverFrames = instance->pendingOutput.size () / static_cast<size_t> (channels);
        if (instance->pendingReadFrame >= leftoverFrames)
        {
            EventList blockEvents (64);
            {
                std::lock_guard<std::mutex> lock (instance->eventMutex);
                for (Event& e : instance->pendingEvents)
                    blockEvents.addEvent (e);
                instance->pendingEvents.clear ();
            }
            instance->processData.inputEvents = &blockEvents;
            instance->processData.outputEvents = nullptr;

            // Drain whatever GUI parameter edits (performEdit -> paramTransfer) arrived since the
            // last block and attach them - without this, IAudioProcessor::process has no way to
            // find out a knob was turned, no matter how correctly everything else is wired.
            instance->inputParamChanges.clearQueue ();
            instance->paramTransfer.transferChangesTo (instance->inputParamChanges);
            instance->processData.inputParameterChanges = &instance->inputParamChanges;

            int32 transferredParamCount = instance->inputParamChanges.getParameterCount ();
            if (transferredParamCount > 0)
                VSTSHIM_LOG ("[VstShim] Process: instance=" << static_cast<void*> (instance)
                          << " transferred " << transferredParamCount << " parameter change(s) into this block.\n");

            if (instance->processor->process (instance->processData) != kResultOk)
            {
                instance->lastError = "process() failed mid-render.";
                return -1;
            }

            instance->pendingOutput.assign (static_cast<size_t> (instance->blockSize) * channels, 0.0f);
            AudioBusBuffers& out = instance->processData.outputs[instance->mainOutputBusIndex];
            for (int32 s = 0; s < instance->blockSize; s++)
                for (int ch = 0; ch < channels; ch++)
                    instance->pendingOutput[static_cast<size_t> (s) * channels + ch] = out.channelBuffers32[ch][s];

            instance->pendingReadFrame = 0;
            leftoverFrames = static_cast<size_t> (instance->blockSize);
        }

        size_t availableFrames = leftoverFrames - instance->pendingReadFrame;
        size_t framesToCopy = std::min<size_t> (availableFrames, static_cast<size_t> (numSamples - framesWritten));

        std::copy (
            instance->pendingOutput.begin () + static_cast<long> (instance->pendingReadFrame * channels),
            instance->pendingOutput.begin () + static_cast<long> ((instance->pendingReadFrame + framesToCopy) * channels),
            outputBuffer + static_cast<size_t> (framesWritten) * channels);

        instance->pendingReadFrame += framesToCopy;
        framesWritten += static_cast<int> (framesToCopy);
    }

    return framesWritten;
}

int VstShim_GetOutputChannelCount (void* handle)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    return instance ? instance->outChannels : 0;
}

const char* VstShim_GetLastError (void* handle)
{
    if (!handle)
        return g_lastLoadError.c_str ();
    return static_cast<ShimInstance*> (handle)->lastError.c_str ();
}

int VstShim_HasEditor (void* handle)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->component)
        return 0;
    TUID controllerCid {};
    return instance->component->getControllerClassId (controllerCid) == kResultTrue ? 1 : 0;
}

int VstShim_OpenEditor (void* handle, void* parentHwnd, int* outWidth, int* outHeight)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->component || !parentHwnd || instance->editorOpen)
        return 0;

    if (!EnsureController (*instance))
        return 0;

    instance->plugView = owned (instance->controller->createView (ViewType::kEditor));
    if (!instance->plugView)
    {
        instance->lastError = "IEditController::createView(kEditor) returned null - plugin has no GUI.";
        instance->controller->terminate ();
        instance->controller = nullptr;
        return 0;
    }

    instance->plugView->setFrame (&instance->editorFrame);

    if (instance->plugView->attached (parentHwnd, kPlatformTypeHWND) != kResultOk)
    {
        instance->lastError = "IPlugView::attached failed.";
        instance->plugView = nullptr;
        instance->controller->terminate ();
        instance->controller = nullptr;
        return 0;
    }

    ViewRect rect {};
    instance->plugView->getSize (&rect);
    if (outWidth)
        *outWidth = rect.getWidth ();
    if (outHeight)
        *outHeight = rect.getHeight ();

    instance->editorOpen = true;
    return 1;
}

void VstShim_CloseEditor (void* handle)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->editorOpen)
        return;

    if (instance->plugView)
    {
        instance->plugView->removed ();
        instance->plugView->setFrame (nullptr);
        instance->plugView = nullptr;
    }
    if (instance->componentConnectionProxy)
    {
        instance->componentConnectionProxy->disconnect ();
        instance->componentConnectionProxy = nullptr;
    }
    if (instance->controllerConnectionProxy)
    {
        instance->controllerConnectionProxy->disconnect ();
        instance->controllerConnectionProxy = nullptr;
    }
    if (instance->controller)
    {
        instance->controller->terminate ();
        instance->controller = nullptr;
    }

    instance->editorOpen = false;
}

int VstShim_DebugGetParameterCount (void* handle)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->component || !EnsureController (*instance))
        return -1;
    return instance->controller->getParameterCount ();
}

int VstShim_DebugGetParameterInfo (void* handle, int index, wchar_t* nameBuffer, int nameBufferSize, unsigned int* outParamId)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->component || !EnsureController (*instance))
        return 0;

    ParameterInfo info {};
    if (instance->controller->getParameterInfo (index, info) != kResultOk)
        return 0;

    if (outParamId)
        *outParamId = info.id;
    if (nameBuffer && nameBufferSize > 0)
    {
        // ParameterInfo::title is String128 (char16_t[128], null-terminated) - same width as
        // wchar_t on Windows, so a direct copy is safe here (this shim is Windows-only anyway).
        int copyLen = std::min<int> (nameBufferSize - 1, 127); // std::min<int>(...), not std::min(...) - windows.h's min() macro would otherwise mangle the unqualified call
        for (int i = 0; i < copyLen; i++)
            nameBuffer[i] = static_cast<wchar_t> (info.title[i]);
        nameBuffer[copyLen] = L'\0';
    }
    return 1;
}

void VstShim_DebugSetParameter (void* handle, unsigned int paramId, double normalizedValue)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->component || !EnsureController (*instance))
        return;

    // The exact same call a plugin's own view makes when the user turns a knob - see
    // ComponentHandlerStub::performEdit above. Triggered programmatically here instead of by a
    // real mouse drag, specifically to verify the parameter-routing fix without needing Pierre at
    // a mouse for this half of the check.
    instance->componentHandlerStub.performEdit (paramId, normalizedValue);
}

const char* VstShim_GetBuildInfo ()
{
    // __DATE__/__TIME__ are stamped at compile time - printed by both --gui and --paramtest at
    // startup specifically to rule out a stale VstShim.dll being the reason a fix doesn't seem
    // to take effect, rather than assuming the freshly-built one is what's actually loaded.
    static const std::string info = std::string ("VstShim built ") + __DATE__ + " " + __TIME__;
    return info.c_str ();
}

void* VstShim_DebugCaptureState (void* handle)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    if (!instance || !instance->component)
        return nullptr;

    auto* stream = new MemoryStream ();
    stream->addRef ();
    if (instance->component->getState (stream) != kResultOk)
    {
        VSTSHIM_LOG ("[VstShim] DebugCaptureState: IComponent::getState failed on instance="
                  << static_cast<void*> (instance) << "\n");
        stream->release ();
        return nullptr;
    }
    VSTSHIM_LOG ("[VstShim] DebugCaptureState: captured " << stream->getSize ()
              << " bytes of component state from instance=" << static_cast<void*> (instance) << "\n");
    return stream;
}

int VstShim_DebugRestoreState (void* handle, void* stateHandle)
{
    auto* instance = static_cast<ShimInstance*> (handle);
    auto* stream = static_cast<MemoryStream*> (stateHandle);
    if (!instance || !instance->component || !stream)
        return 0;

    int64 seekResult = 0;
    stream->seek (0, IBStream::kIBSeekSet, &seekResult);

    // Testing IComponent::setState specifically (item 4 of the investigation) - separate from,
    // and independent of, the performEdit/paramTransfer path fixed above. If this measurably
    // changes the audio but a real patch load in the actual editor still doesn't, that would
    // point at the plugin's patch-browser using some third mechanism instead of either of these -
    // but if this ALSO does nothing, that's strong evidence setState itself isn't reaching the
    // live processing instance.
    tresult result = instance->component->setState (stream);
    VSTSHIM_LOG ("[VstShim] DebugRestoreState: IComponent::setState on instance="
              << static_cast<void*> (instance) << " returned " << result << "\n");
    return result == kResultOk ? 1 : 0;
}
