// PHASE4-VST3-NATIVE.md Milestone 1+2: prove the native shim can link against the VST3 SDK,
// load a plugin module, enumerate its classes, instantiate an IComponent (Milestone 1), then
// drive it through VST3's setup/activate/process sequence and render one block of audio to a
// WAV file (Milestone 2 - no MIDI/note yet, so silence is expected and fine). See
// VstNativeShim/PROGRESS-NATIVE.md for the write-up of both.

#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/hosting/eventlist.h"
#include "public.sdk/source/vst/utility/stringconvert.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/ivstevents.h"
#include "wavwriter.h"

#include <iostream>
#include <string>
#include <vector>
#include <algorithm>

using namespace VST3::Hosting;
using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {
constexpr double kSampleRate = 48000.0;
constexpr int32 kBlockSize = 512;

// ~1s sustain + ~0.5s release/decay tail, in whole blocks.
constexpr int32 kSustainBlocks = 94;
constexpr int32 kReleaseBlocks = 47;

std::string ToUtf8 (const String128& utf16Name)
{
    return StringConvert::convert (std::u16string (reinterpret_cast<const char16_t*> (utf16Name)));
}
} // namespace

namespace {

// FUID from Module::create's ClassInfo::ID() is VST3::UID; PluginFactory::createInstance wants
// the same UID type, so no conversion needed - just documenting the type for readability.

void PrintClassInfo (const ClassInfo& info, int index)
{
    std::cout << "  [" << index << "] " << info.name ()
              << "  (category=" << info.category ()
              << ", vendor=" << info.vendor ()
              << ", version=" << info.version ()
              << ", subCategories=" << info.subCategoriesString () << ")\n";
}

} // namespace

int main (int argc, char** argv)
{
    std::string pluginPath = argc > 1
        ? argv[1]
        : R"(C:\Program Files\Common Files\VST3\Surge Synth Team\Surge XT.vst3)";

    std::cout << "Loading module: " << pluginPath << "\n";

    std::string error;
    Module::Ptr module = Module::create (pluginPath, error);
    if (!module)
    {
        std::cerr << "FAILED to load module: " << error << "\n";
        return 1;
    }

    std::cout << "Module loaded OK. Name: " << module->getName () << "\n";

    const PluginFactory& factory = module->getFactory ();
    FactoryInfo factoryInfo = factory.info ();
    std::cout << "Factory vendor: " << factoryInfo.vendor ()
              << "  url: " << factoryInfo.url () << "\n";

    PluginFactory::ClassInfos classInfos = factory.classInfos ();
    std::cout << "Class count: " << classInfos.size () << "\n";

    int index = 0;
    const ClassInfo* audioEffectClass = nullptr;
    for (const ClassInfo& info : classInfos)
    {
        PrintClassInfo (info, index++);
        if (!audioEffectClass && info.category () == kVstAudioEffectClass)
            audioEffectClass = &info;
    }

    if (!audioEffectClass)
    {
        std::cerr << "No class with category '" << kVstAudioEffectClass << "' found - nothing to instantiate.\n";
        return 2;
    }

    std::cout << "\nInstantiating: " << audioEffectClass->name () << " (" << audioEffectClass->subCategoriesString () << ")\n";

    IPtr<IComponent> component = factory.createInstance<IComponent> (audioEffectClass->ID ());
    if (!component)
    {
        std::cerr << "FAILED: createInstance<IComponent> returned null.\n";
        return 3;
    }

    std::cout << "createInstance<IComponent> succeeded (non-null).\n";

    // queryInterface round-trip, separate from createInstance's own template cast, to prove the
    // returned object really does answer to IComponent's IID via COM-style queryInterface too
    // (createInstance<T> internally does the same thing, but proving it explicitly matches what
    // the milestone asked for).
    IComponent* queried = nullptr;
    tresult qiResult = component->queryInterface (IComponent::iid, reinterpret_cast<void**> (&queried));
    if (qiResult != kResultOk || !queried)
    {
        std::cerr << "FAILED: explicit queryInterface(IComponent::iid) did not succeed (result=" << qiResult << ").\n";
        return 4;
    }
    queried->release ();

    std::cout << "Explicit queryInterface<IComponent> succeeded too.\n";
    std::cout << "Milestone 1: SUCCESS.\n";

    // ---- Milestone 2: setup sequence + render one block ----

    HostApplication hostContext;

    tresult initResult = component->initialize (&hostContext);
    if (initResult != kResultOk)
    {
        std::cerr << "FAILED: IComponent::initialize returned " << initResult << ".\n";
        return 5;
    }
    std::cout << "\nIComponent::initialize succeeded.\n";

    IPtr<IAudioProcessor> processor;
    {
        IAudioProcessor* rawProcessor = nullptr;
        if (component->queryInterface (IAudioProcessor::iid, reinterpret_cast<void**> (&rawProcessor)) != kResultOk || !rawProcessor)
        {
            std::cerr << "FAILED: component does not implement IAudioProcessor.\n";
            component->terminate ();
            return 6;
        }
        processor = owned (rawProcessor);
    }
    std::cout << "queryInterface<IAudioProcessor> succeeded.\n";

    if (processor->canProcessSampleSize (kSample32) != kResultOk)
    {
        std::cerr << "FAILED: plugin does not support 32-bit float sample processing.\n";
        component->terminate ();
        return 7;
    }

    ProcessSetup setup {};
    setup.processMode = kRealtime;
    setup.symbolicSampleSize = kSample32;
    setup.maxSamplesPerBlock = kBlockSize;
    setup.sampleRate = kSampleRate;

    if (processor->setupProcessing (setup) != kResultOk)
    {
        std::cerr << "FAILED: IAudioProcessor::setupProcessing did not return kResultOk.\n";
        component->terminate ();
        return 8;
    }
    std::cout << "setupProcessing succeeded (sampleRate=" << kSampleRate << ", blockSize=" << kBlockSize << ").\n";

    int32 numInputBusses = component->getBusCount (kAudio, kInput);
    int32 numOutputBusses = component->getBusCount (kAudio, kOutput);
    std::cout << "Audio busses: " << numInputBusses << " input, " << numOutputBusses << " output.\n";

    // Milestone 3 step 0 (per the review of Milestone 2's finding): don't assume bus 0 is the
    // main output - ask each output bus's BusInfo::busType and use the first one that says kMain.
    int32 mainOutputBusIndex = -1;
    for (int32 i = 0; i < numOutputBusses; i++)
    {
        BusInfo busInfo {};
        if (component->getBusInfo (kAudio, kOutput, i, busInfo) == kResultOk)
        {
            std::cout << "  Output bus " << i << ": \"" << ToUtf8 (busInfo.name) << "\""
                       << "  channels=" << busInfo.channelCount
                       << "  busType=" << (busInfo.busType == kMain ? "kMain" : "kAux") << "\n";
            if (mainOutputBusIndex < 0 && busInfo.busType == kMain)
                mainOutputBusIndex = i;
        }
    }
    if (mainOutputBusIndex < 0)
    {
        std::cerr << "FAILED: no output bus reported busType == kMain.\n";
        component->terminate ();
        return 13;
    }
    std::cout << "Main output bus is index " << mainOutputBusIndex << ".\n";

    // Find the event (MIDI-equivalent) input bus so Note On/Off events target the right busIndex
    // in the Event struct - not to be confused with the audio bus indices above.
    int32 numEventInputBusses = component->getBusCount (kEvent, kInput);
    int32 eventInputBusIndex = -1;
    for (int32 i = 0; i < numEventInputBusses; i++)
    {
        BusInfo busInfo {};
        if (component->getBusInfo (kEvent, kInput, i, busInfo) == kResultOk)
        {
            std::cout << "  Event input bus " << i << ": \"" << ToUtf8 (busInfo.name) << "\""
                       << "  channelCount(MIDI channels)=" << busInfo.channelCount << "\n";
            if (eventInputBusIndex < 0)
                eventInputBusIndex = i;
        }
    }
    if (eventInputBusIndex < 0)
    {
        std::cerr << "FAILED: no event input bus found - nothing to send Note On/Off through.\n";
        component->terminate ();
        return 14;
    }
    std::cout << "Using event input bus index " << eventInputBusIndex << " for Note On/Off.\n";

    for (int32 i = 0; i < numInputBusses; i++)
        component->activateBus (kAudio, kInput, i, true);
    for (int32 i = 0; i < numOutputBusses; i++)
        component->activateBus (kAudio, kOutput, i, true);
    component->activateBus (kEvent, kInput, eventInputBusIndex, true);
    std::cout << "Activated all audio busses + the event input bus.\n";

    if (component->setActive (true) != kResultOk)
    {
        std::cerr << "FAILED: IComponent::setActive(true) did not return kResultOk.\n";
        component->terminate ();
        return 9;
    }
    std::cout << "setActive(true) succeeded.\n";

    if (processor->setProcessing (true) != kResultOk)
    {
        std::cerr << "FAILED: IAudioProcessor::setProcessing(true) did not return kResultOk.\n";
        component->setActive (false);
        component->terminate ();
        return 10;
    }
    std::cout << "setProcessing(true) succeeded.\n";

    HostProcessData processData;
    if (!processData.prepare (*component, kBlockSize, kSample32))
    {
        std::cerr << "FAILED: HostProcessData::prepare returned false.\n";
        processor->setProcessing (false);
        component->setActive (false);
        component->terminate ();
        return 11;
    }
    processData.numSamples = kBlockSize;
    processData.symbolicSampleSize = kSample32;
    processData.processMode = kRealtime;

    ProcessContext context {};
    context.state = ProcessContext::kTempoValid | ProcessContext::kTimeSigValid;
    context.sampleRate = kSampleRate;
    context.tempo = 120.0;
    context.timeSigNumerator = 4;
    context.timeSigDenominator = 4;
    processData.processContext = &context;

    int outChannels = processData.outputs[mainOutputBusIndex].numChannels;
    std::cout << "Main output bus channel count: " << outChannels << "\n";
    if (outChannels <= 0)
    {
        std::cerr << "FAILED: main output bus reports 0 channels.\n";
        processor->setProcessing (false);
        component->setActive (false);
        component->terminate ();
        return 15;
    }

    WavWriter wav (outChannels, static_cast<int> (kSampleRate));
    EventList inputEvents (4);
    float peakAbsSampleOverall = 0.0f;
    int32 totalBlocks = 1 /*note-on block*/ + kSustainBlocks + kReleaseBlocks;

    constexpr int16 kPitch = 60; // Middle C
    constexpr float kVelocity = 0.85f;
    constexpr int32 kNoteId = 1;

    for (int32 block = 0; block < totalBlocks; block++)
    {
        inputEvents.clear ();

        if (block == 0)
        {
            Event noteOn {};
            noteOn.busIndex = eventInputBusIndex;
            noteOn.sampleOffset = 0;
            noteOn.type = Event::kNoteOnEvent;
            noteOn.noteOn.channel = 0;
            noteOn.noteOn.pitch = kPitch;
            noteOn.noteOn.tuning = 0.0f;
            noteOn.noteOn.velocity = kVelocity;
            noteOn.noteOn.length = 0;
            noteOn.noteOn.noteId = kNoteId;
            inputEvents.addEvent (noteOn);
        }
        else if (block == 1 + kSustainBlocks)
        {
            Event noteOff {};
            noteOff.busIndex = eventInputBusIndex;
            noteOff.sampleOffset = 0;
            noteOff.type = Event::kNoteOffEvent;
            noteOff.noteOff.channel = 0;
            noteOff.noteOff.pitch = kPitch;
            noteOff.noteOff.velocity = 0.0f;
            noteOff.noteOff.noteId = kNoteId;
            noteOff.noteOff.tuning = 0.0f;
            inputEvents.addEvent (noteOff);
        }

        processData.inputEvents = &inputEvents;
        processData.outputEvents = nullptr;

        tresult processResult = processor->process (processData);
        if (processResult != kResultOk)
        {
            std::cerr << "FAILED: IAudioProcessor::process returned " << processResult
                       << " on block " << block << ".\n";
            processor->setProcessing (false);
            component->setActive (false);
            component->terminate ();
            return 16;
        }

        std::vector<float> interleaved (static_cast<size_t> (kBlockSize) * outChannels);
        for (int32 s = 0; s < kBlockSize; s++)
        {
            for (int ch = 0; ch < outChannels; ch++)
            {
                float sample = processData.outputs[mainOutputBusIndex].channelBuffers32[ch][s];
                peakAbsSampleOverall = std::max (peakAbsSampleOverall, std::abs (sample));
                interleaved[static_cast<size_t> (s) * outChannels + ch] = sample;
            }
        }
        wav.AppendBlock (interleaved);
    }

    std::cout << "Processed " << totalBlocks << " blocks (1 note-on + " << kSustainBlocks
               << " sustain + " << kReleaseBlocks << " release/decay).\n";
    std::cout << "Peak abs sample value across the whole sequence: " << peakAbsSampleOverall
               << (peakAbsSampleOverall > 0.0f ? " (non-silent - good)" : " (SILENT - unexpected, note did not sound)") << "\n";

    std::string outPath = "milestone3_output.wav";
    if (wav.WriteFile (outPath))
        std::cout << "Wrote " << wav.TotalFrames () << " frames ("
                   << (wav.TotalFrames () / kSampleRate) << "s) to " << outPath << "\n";
    else
        std::cerr << "FAILED to write " << outPath << "\n";

    // Clean shutdown, mirroring the VST3 lifecycle in reverse.
    processor->setProcessing (false);
    component->activateBus (kEvent, kInput, eventInputBusIndex, false);
    for (int32 i = 0; i < numOutputBusses; i++)
        component->activateBus (kAudio, kOutput, i, false);
    for (int32 i = 0; i < numInputBusses; i++)
        component->activateBus (kAudio, kInput, i, false);
    component->setActive (false);
    component->terminate ();

    std::cout << "\nMilestone 3: " << (peakAbsSampleOverall > 0.0f ? "SUCCESS (non-silent output produced - Pierre must confirm it sounds correct)" : "FAILED (silent output)") << ".\n";
    return peakAbsSampleOverall > 0.0f ? 0 : 17;
}
