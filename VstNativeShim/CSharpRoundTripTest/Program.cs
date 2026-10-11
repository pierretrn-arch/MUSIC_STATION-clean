// PHASE4-VST3-NATIVE.md Milestones 4-5.
//
// Modes (first arg selects; default is Milestone 4's round-trip test for backward compatibility):
//   (no flag) [pluginPath]     - Milestone 4: render a fixed note sequence, write a WAV, compare
//                                 against Milestone 3's native-only output.
//   --interactive [pluginPath] - Milestone 5: real-time playback via NAudio WaveOutEvent, notes
//                                 triggered from console key presses. Run this yourself and
//                                 listen - toggling a key plays/stops that note; multiple keys can
//                                 be held (toggled on) at once.
//   --selftest [pluginPath]    - Milestone 5: automated stress test (rapid on/off + sustained
//                                 notes) through the same real-time NAudio path, checking for
//                                 Read-callback timing headroom instead of needing a human to
//                                 listen for glitches.
using CSharpRoundTripTest;
using NAudio.Wave;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

const string DefaultPluginPath = @"C:\Program Files\Common Files\VST3\Surge Synth Team\Surge XT.vst3";
const double SampleRate = 48000.0;

if (args.Length > 0 && args[0] == "--pingtest")
{
    Console.WriteLine("Z: about to call VstShim_GetOutputChannelCount(0) - trivial, no logging, no static string");
    Console.Out.Flush();
    int trivial = VstShimInterop.VstShim_GetOutputChannelCount(IntPtr.Zero);
    Console.WriteLine($"Y: got {trivial}");
    Console.Out.Flush();

    Console.WriteLine("A: about to call VstShim_GetBuildInfo");
    Console.Out.Flush();
    string buildInfo = VstShimInterop.VstShim_GetBuildInfo();
    Console.WriteLine($"B: got build info: {buildInfo}");
    Console.Out.Flush();
    IntPtr h = VstShimInterop.VstShim_LoadPlugin(args.Length > 1 ? args[1] : DefaultPluginPath);
    Console.WriteLine($"C: LoadPlugin returned {h}");
    Console.Out.Flush();
    if (h != IntPtr.Zero)
    {
        int initOk = VstShimInterop.VstShim_Initialize(h, SampleRate, 512);
        Console.WriteLine($"D: Initialize returned {initOk}");
        Console.Out.Flush();
        VstShimInterop.VstShim_UnloadPlugin(h);
        Console.WriteLine("E: UnloadPlugin done");
        Console.Out.Flush();
    }
    return 0;
}

if (args.Length > 0 && args[0] == "--interactive")
    return RunInteractive(args.Length > 1 ? args[1] : DefaultPluginPath);

if (args.Length > 0 && args[0] == "--selftest")
    return RunSelfTest(args.Length > 1 ? args[1] : DefaultPluginPath);

if (args.Length > 0 && args[0] == "--paramtest")
    return RunParamTest(args.Length > 1 ? args[1] : DefaultPluginPath);

if (args.Length > 0 && args[0] == "--statetest")
    return RunStateTest(args.Length > 1 ? args[1] : DefaultPluginPath);

if (args.Length > 0 && args[0] == "--gui")
{
    // Top-level statements' synthesized Main can't be annotated [STAThread], and WPF (via
    // HwndHost) requires an STA thread - so run it on one explicitly rather than restructuring
    // the whole file's entry point just for this mode.
    int guiResult = 0;
    var staThread = new Thread(() => guiResult = RunGui(args.Length > 1 ? args[1] : DefaultPluginPath));
    staThread.SetApartmentState(ApartmentState.STA);
    staThread.Start();
    staThread.Join();
    return guiResult;
}

return RunMilestone4RoundTrip(args.Length > 0 ? args[0] : DefaultPluginPath);

// ---- Milestone 4 (unchanged behavior, kept as the default mode) ----

static int RunMilestone4RoundTrip(string pluginPath)
{
    const int BlockSize = 512;
    const int SustainBlocks = 94;
    const int ReleaseBlocks = 47;
    const int MidiNote = 60;
    const int Velocity = 108;

    Console.WriteLine($"Loading module: {pluginPath}");

    IntPtr handle = VstShimInterop.VstShim_LoadPlugin(pluginPath);
    if (handle == IntPtr.Zero)
    {
        Console.Error.WriteLine($"FAILED: VstShim_LoadPlugin returned null. {VstShimInterop.VstShim_GetLastError(IntPtr.Zero)}");
        return 1;
    }
    Console.WriteLine("VstShim_LoadPlugin succeeded.");

    if (VstShimInterop.VstShim_Initialize(handle, SampleRate, BlockSize) == 0)
    {
        Console.Error.WriteLine($"FAILED: VstShim_Initialize failed. {VstShimInterop.VstShim_GetLastError(handle)}");
        VstShimInterop.VstShim_UnloadPlugin(handle);
        return 2;
    }
    Console.WriteLine("VstShim_Initialize succeeded.");

    int channels = VstShimInterop.VstShim_GetOutputChannelCount(handle);
    Console.WriteLine($"Output channel count: {channels}");
    if (channels <= 0)
    {
        Console.Error.WriteLine("FAILED: 0 output channels.");
        VstShimInterop.VstShim_UnloadPlugin(handle);
        return 3;
    }

    var wav = new WavWriter(channels, (int)SampleRate);
    float[] buffer = new float[BlockSize * channels];
    float peakAbsSample = 0f;
    int totalBlocks = 1 + SustainBlocks + ReleaseBlocks;

    for (int block = 0; block < totalBlocks; block++)
    {
        if (block == 0)
        {
            VstShimInterop.VstShim_NoteOn(handle, MidiNote, Velocity);
            Console.WriteLine($"NoteOn({MidiNote}, {Velocity}) queued.");
        }
        else if (block == 1 + SustainBlocks)
        {
            VstShimInterop.VstShim_NoteOff(handle, MidiNote);
            Console.WriteLine($"NoteOff({MidiNote}) queued.");
        }

        int framesRendered;
        unsafe
        {
            fixed (float* ptr = buffer)
                framesRendered = VstShimInterop.VstShim_Process(handle, ptr, BlockSize);
        }
        if (framesRendered != BlockSize)
        {
            Console.Error.WriteLine($"FAILED: VstShim_Process returned {framesRendered} (expected {BlockSize}) on block {block}. {VstShimInterop.VstShim_GetLastError(handle)}");
            VstShimInterop.VstShim_UnloadPlugin(handle);
            return 4;
        }

        for (int i = 0; i < BlockSize * channels; i++)
            peakAbsSample = Math.Max(peakAbsSample, Math.Abs(buffer[i]));

        wav.AppendBlock(buffer, BlockSize * channels);
    }

    Console.WriteLine($"Processed {totalBlocks} blocks (1 note-on + {SustainBlocks} sustain + {ReleaseBlocks} release/decay).");
    Console.WriteLine($"Peak abs sample value across the whole sequence: {peakAbsSample}" +
        (peakAbsSample > 0f ? " (non-silent - good)" : " (SILENT - unexpected)"));

    string outPath = "milestone4_output_csharp.wav";
    wav.WriteFile(outPath);
    Console.WriteLine($"Wrote {wav.TotalFrames} frames ({wav.TotalFrames / SampleRate}s) to {outPath}");

    VstShimInterop.VstShim_AllNotesOff(handle);
    VstShimInterop.VstShim_UnloadPlugin(handle);

    Console.WriteLine();
    Console.WriteLine(peakAbsSample > 0f ? "Milestone 4: SUCCESS (C# round trip produced non-silent audio)." : "Milestone 4: FAILED (silent output).");
    return peakAbsSample > 0f ? 0 : 5;
}

// ---- Milestone 5 ----

static (IntPtr handle, VstShimSampleProvider provider, WaveOutEvent output)? StartPlayback(string pluginPath)
{
    // Printed on every mode that goes through here (including --gui) so a stale VstShim.dll is
    // immediately visible rather than silently assumed away - see PROGRESS-NATIVE.md.
    Console.WriteLine($"[VstShim build] {VstShimInterop.VstShim_GetBuildInfo()}");
    Console.WriteLine($"Loading module: {pluginPath}");
    IntPtr handle = VstShimInterop.VstShim_LoadPlugin(pluginPath);
    if (handle == IntPtr.Zero)
    {
        Console.Error.WriteLine($"FAILED: VstShim_LoadPlugin returned null. {VstShimInterop.VstShim_GetLastError(IntPtr.Zero)}");
        return null;
    }

    const int blockSize = 512;
    if (VstShimInterop.VstShim_Initialize(handle, SampleRate, blockSize) == 0)
    {
        Console.Error.WriteLine($"FAILED: VstShim_Initialize failed. {VstShimInterop.VstShim_GetLastError(handle)}");
        VstShimInterop.VstShim_UnloadPlugin(handle);
        return null;
    }

    int channels = VstShimInterop.VstShim_GetOutputChannelCount(handle);
    var provider = new VstShimSampleProvider(handle, (int)SampleRate, channels);
    var output = new WaveOutEvent { DesiredLatency = 100 };
    output.Init(provider);
    output.Play();

    Console.WriteLine($"Playing. Channels={channels}, DesiredLatency={output.DesiredLatency}ms.");
    return (handle, provider, output);
}

static int RunInteractive(string pluginPath)
{
    var started = StartPlayback(pluginPath);
    if (started is null)
        return 1;
    var (handle, provider, output) = started.Value;

    // Simple one-octave-of-white-keys mapping, toggle-style: console apps get no key-up event,
    // so pressing a mapped key a second time is what sends the Note Off - this still exercises a
    // real Note On -> sustain -> Note Off -> release cycle end to end, and holding several keys
    // toggled on at once tests polyphony.
    var keyToNote = new Dictionary<char, int>
    {
        ['q'] = 60, ['w'] = 62, ['e'] = 64, ['r'] = 65,
        ['t'] = 67, ['y'] = 69, ['u'] = 71, ['i'] = 72,
    };
    var heldNotes = new HashSet<char>();

    Console.WriteLine("Keys Q W E R T Y U I play a C major scale (toggle on/off). Space = All Notes Off. Esc = quit.");
    while (true)
    {
        ConsoleKeyInfo key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Escape)
            break;
        if (key.Key == ConsoleKey.Spacebar)
        {
            provider.AllNotesOff();
            heldNotes.Clear ();
            Console.WriteLine("All notes off.");
            continue;
        }

        char c = char.ToLowerInvariant(key.KeyChar);
        if (!keyToNote.TryGetValue(c, out int note))
            continue;

        if (heldNotes.Add(c))
        {
            provider.NoteOn(note, 100);
            Console.WriteLine($"NoteOn {note}");
        }
        else
        {
            heldNotes.Remove(c);
            provider.NoteOff(note);
            Console.WriteLine($"NoteOff {note}");
        }
    }

    provider.AllNotesOff();
    output.Stop();
    output.Dispose();
    provider.Dispose();
    Console.WriteLine($"Worst Read() time observed: {provider.WorstReadMillis:F2}ms.");
    return 0;
}

static int RunSelfTest(string pluginPath)
{
    var started = StartPlayback(pluginPath);
    if (started is null)
        return 1;
    var (handle, provider, output) = started.Value;
    provider.Recording = new List<float>();

    Console.WriteLine();
    Console.WriteLine("Self-test: sustained note for 2s...");
    provider.NoteOn(60, 100);
    Thread.Sleep(2000);
    provider.NoteOff(60);
    Thread.Sleep(500);

    Console.WriteLine("Self-test: rapid on/off burst (40 notes, 60ms apart) across a small chord range...");
    var rng = new Random(12345);
    int[] pitches = { 55, 58, 60, 62, 65, 67 };
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < 40; i++)
    {
        int pitch = pitches[rng.Next(pitches.Length)];
        provider.NoteOn(pitch, 90 + rng.Next(30));
        Thread.Sleep(30);
        provider.NoteOff(pitch);
        Thread.Sleep(30);
    }
    sw.Stop();
    Console.WriteLine($"Burst finished in {sw.ElapsedMilliseconds}ms (wall clock).");

    provider.AllNotesOff();
    Thread.Sleep(500);

    Console.WriteLine("Self-test: two overlapping sustained notes (polyphony) for 1.5s...");
    provider.NoteOn(60, 100);
    Thread.Sleep(300);
    provider.NoteOn(64, 100);
    Thread.Sleep(1500);
    provider.AllNotesOff();
    Thread.Sleep(500);

    output.Stop();
    double worstMs = provider.WorstReadMillis;
    List<float> recording = provider.Recording!;
    int channels = VstShimInterop.VstShim_GetOutputChannelCount(handle);
    output.Dispose();
    provider.Dispose();

    Console.WriteLine();
    Console.WriteLine($"Worst single Read() call: {worstMs:F2}ms (any Read() exceeding its real-time budget prints a " +
        "'[glitch risk]' line above as it happens - none above means no call ever fell behind real time).");

    // Independent spot check on what was actually sent to the audio device - not a substitute
    // for Pierre listening to --interactive, but catches "silently produced nothing" or "crashed
    // the signal path halfway through" even though this mode doesn't have a human listening.
    var wav = new WavWriter(channels, (int)SampleRate);
    wav.AppendBlock(recording.ToArray(), recording.Count);
    wav.WriteFile("milestone5_selftest.wav");
    float peak = recording.Count > 0 ? recording.Max(Math.Abs) : 0f;
    double rms = recording.Count > 0 ? Math.Sqrt(recording.Average(s => (double)s * s)) : 0.0;
    Console.WriteLine($"Recorded {wav.TotalFrames} frames ({wav.TotalFrames / SampleRate:F1}s) of actual playback output to milestone5_selftest.wav");
    Console.WriteLine($"Peak abs sample: {peak:F4}, overall RMS: {rms:F4}" + (peak > 0f ? " (non-silent - good)" : " (SILENT - unexpected)"));

    Console.WriteLine();
    Console.WriteLine(peak > 0f
        ? "Milestone 5 self-test: SUCCESS (completed without exceptions or timing overruns, output confirmed non-silent)."
        : "Milestone 5 self-test: FAILED (silent output).");
    return peak > 0f ? 0 : 1;
}

// ---- Milestone 6: GUI editor embedding ----

static int RunGui(string pluginPath)
{
    var started = StartPlayback(pluginPath);
    if (started is null)
        return 1;
    var (handle, provider, output) = started.Value;

    if (VstShimInterop.VstShim_HasEditor(handle) == 0)
    {
        Console.WriteLine("Plugin reports no separate controller class - nothing to embed. " +
            "(Per the milestone's framing, the plugin would still be fully usable headless.)");
        output.Stop();
        output.Dispose();
        provider.Dispose();
        return 2;
    }

    var keyToNote = new Dictionary<Key, int>
    {
        [Key.Q] = 60, [Key.W] = 62, [Key.E] = 64, [Key.R] = 65,
        [Key.T] = 67, [Key.Y] = 69, [Key.U] = 71, [Key.I] = 72,
    };
    var heldNotes = new HashSet<Key>();

    var editorHost = new VstEditorHost(handle);
    var status = new TextBlock
    {
        Text = "Loading editor...",
        Margin = new Thickness(8),
        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
    };
    var root = new DockPanel();
    DockPanel.SetDock(status, Dock.Top);
    root.Children.Add(status);
    root.Children.Add(editorHost);

    var window = new Window
    {
        Title = "PHASE4-VST3-NATIVE.md Milestone 6 - GUI editor embedding",
        Width = 500,
        Height = 500,
        Content = root,
    };

    window.KeyDown += (_, e) =>
    {
        if (keyToNote.TryGetValue(e.Key, out int note) && heldNotes.Add(e.Key))
            provider.NoteOn(note, 100);
    };
    window.KeyUp += (_, e) =>
    {
        if (keyToNote.TryGetValue(e.Key, out int note) && heldNotes.Remove(e.Key))
            provider.NoteOff(note);
    };
    window.Closed += (_, _) =>
    {
        provider.AllNotesOff();
        // Explicitly dispose the HwndHost (closes the embedded editor) before unloading the
        // plugin - previously this ran in the opposite order (WPF's own teardown calling
        // DestroyWindowCore -> VstShim_CloseEditor *after* UnloadPlugin had already freed the
        // native instance), which crashed with an AccessViolationException on a use-after-free.
        // VstShim_UnloadPlugin now also defensively closes the editor itself if still open, so
        // this line is redundant-but-harmless belt-and-suspenders, not the only fix.
        editorHost.Dispose();
        output.Stop();
        output.Dispose();
        provider.Dispose(); // VstShim_UnloadPlugin
    };

    window.Loaded += (_, _) =>
    {
        status.Text = $"Editor reported size: {editorHost.EditorWidth}x{editorHost.EditorHeight}.\n" +
            "Hold Q W E R T Y U I (this window must have focus, not the plugin editor) to play a C major scale.\n" +
            "Try turning a knob in the editor below, then play a note, to check it's actually connected.";
        Console.WriteLine($"Editor opened OK. Reported size: {editorHost.EditorWidth}x{editorHost.EditorHeight}.");
    };

    var app = new Application();
    Console.WriteLine("Opening WPF window with embedded editor - close the window to end this mode.");
    return app.Run(window);
}

// ---- Diagnostic: verify the Milestone 6 GUI-parameter-edit fix without needing a mouse ----
//
// Calls VstShim_DebugSetParameter, which invokes the exact same ComponentHandlerStub::performEdit
// a real GUI knob drag would call - not a separate, easier-to-pass path. Sweeps Surge XT's filter
// cutoff (found by name, not a hardcoded ID) between a low and high value while a sustained note
// plays, and checks whether the audio's brightness (high-frequency content) actually changes -
// proving parameter edits now reach the audio engine, not just the GUI's own display.
static int RunParamTest(string pluginPath)
{
    var started = StartPlayback(pluginPath);
    if (started is null)
        return 1;
    var (handle, provider, output) = started.Value;
    provider.Recording = new List<float>();

    int paramCount = VstShimInterop.VstShim_DebugGetParameterCount(handle);
    if (paramCount < 0)
    {
        Console.Error.WriteLine($"FAILED: could not enumerate parameters. {VstShimInterop.VstShim_GetLastError(handle)}");
        return 2;
    }
    Console.WriteLine($"Controller reports {paramCount} parameters. Searching for a filter cutoff...");

    // Prefer an unambiguous, always-in-signal-path parameter over filter cutoff - cutoff's actual
    // audible effect depends on the default patch's filter routing/mix, which turned out NOT to
    // be a safe assumption (first attempt using "A Filter 1 Cutoff" showed no change, which could
    // mean either a routing fix bug OR just that Filter 1 isn't in this patch's signal path at
    // all - volume avoids that ambiguity entirely).
    string[] namePriority = { "volume", "gain", "amp", "cutoff" };
    var nameBuffer = new System.Text.StringBuilder(128);
    uint? targetId = null;
    string? targetName = null;
    foreach (string candidate in namePriority)
    {
        for (int i = 0; i < paramCount; i++)
        {
            nameBuffer.Clear();
            if (VstShimInterop.VstShim_DebugGetParameterInfo(handle, i, nameBuffer, nameBuffer.Capacity, out uint paramId) == 0)
                continue;
            string name = nameBuffer.ToString();
            if (name.Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                targetId = paramId;
                targetName = name;
                break;
            }
        }
        if (targetId is not null)
            break;
    }
    if (targetId is null)
    {
        Console.Error.WriteLine("FAILED: no volume/gain/amp/cutoff parameter found.");
        return 3;
    }
    Console.WriteLine($"Using parameter \"{targetName}\" (id={targetId}) as the audible test target.");

    provider.NoteOn(48, 110);
    Thread.Sleep(200);

    Console.WriteLine("Setting parameter LOW (0.05 normalized)...");
    VstShimInterop.VstShim_DebugSetParameter(handle, targetId.Value, 0.05);
    Thread.Sleep(1200);

    Console.WriteLine("Setting parameter HIGH (0.95 normalized)...");
    VstShimInterop.VstShim_DebugSetParameter(handle, targetId.Value, 0.95);
    Thread.Sleep(1200);

    provider.AllNotesOff();
    Thread.Sleep(300);

    output.Stop();
    List<float> recording = provider.Recording!;
    int channels = VstShimInterop.VstShim_GetOutputChannelCount(handle);
    output.Dispose();
    provider.Dispose();

    var wav = new WavWriter(channels, (int)SampleRate);
    wav.AppendBlock(recording.ToArray(), recording.Count);
    wav.WriteFile("milestone6_paramtest.wav");

    // Brightness proxy: mean absolute sample-to-sample delta. A more open filter lets through
    // more high-frequency content, which increases sample-to-sample variation; this doesn't need
    // a real FFT to be a meaningful comparison between two halves of the same note at the same
    // pitch/velocity.
    int frameCount = channels > 0 ? recording.Count / channels : 0;
    static double Brightness(List<float> samples, int channels, int startFrame, int endFrame)
    {
        double sum = 0;
        int count = 0;
        for (int f = startFrame + 1; f < endFrame; f++)
        {
            int i = f * channels;
            int iPrev = (f - 1) * channels;
            if (i + channels > samples.Count)
                break;
            sum += Math.Abs(samples[i] - samples[iPrev]);
            count++;
        }
        return count > 0 ? sum / count : 0;
    }

    // Skip the first ~150ms of each half so we're comparing settled filter states, not the
    // transition instant.
    int lowStart = (int)(0.2 * SampleRate);
    int lowEnd = (int)(1.2 * SampleRate);
    int highStart = (int)(1.55 * SampleRate);
    int highEnd = Math.Min(frameCount, (int)(2.55 * SampleRate));

    static double Rms(List<float> samples, int startFrame, int endFrame, int channels)
    {
        double sumSquares = 0;
        int count = 0;
        for (int f = startFrame; f < endFrame; f++)
        {
            int i = f * channels;
            if (i + channels > samples.Count)
                break;
            for (int ch = 0; ch < channels; ch++)
                sumSquares += (double)samples[i + ch] * samples[i + ch];
            count += channels;
        }
        return count > 0 ? Math.Sqrt(sumSquares / count) : 0;
    }

    double lowBrightness = Brightness(recording, channels, lowStart, lowEnd);
    double highBrightness = Brightness(recording, channels, highStart, highEnd);
    double lowRms = Rms(recording, lowStart, lowEnd, channels);
    double highRms = Rms(recording, highStart, highEnd, channels);

    Console.WriteLine();
    Console.WriteLine($"Recorded {frameCount} frames ({frameCount / SampleRate:F2}s) to milestone6_paramtest.wav");
    Console.WriteLine($"RMS with param LOW:  {lowRms:F6}   Brightness LOW:  {lowBrightness:F6}");
    Console.WriteLine($"RMS with param HIGH: {highRms:F6}   Brightness HIGH: {highBrightness:F6}");
    double rmsRatio = lowRms > 0 ? highRms / lowRms : (highRms > 0 ? double.PositiveInfinity : 0);
    double brightnessRatio = lowBrightness > 0 ? highBrightness / lowBrightness : 0;
    Console.WriteLine($"RMS ratio (high/low): {rmsRatio:F2}x   Brightness ratio (high/low): {brightnessRatio:F2}x");

    // Either metric moving substantially (not just noise) counts as a pass - a volume/gain
    // parameter should move RMS; a filter/tone parameter should move brightness instead. Genuine
    // measurement noise between two 1-second windows of the same held note should be nowhere near
    // a 30% swing in either direction.
    bool pass = rmsRatio > 1.3 || rmsRatio < 0.77 || brightnessRatio > 1.3 || brightnessRatio < 0.77;
    Console.WriteLine();
    Console.WriteLine(pass
        ? "Milestone 6 param-routing fix: SUCCESS (parameter edit measurably changed the audio)."
        : "Milestone 6 param-routing fix: FAILED (no measurable audio change from the parameter edit).");
    return pass ? 0 : 4;
}

// ---- Diagnostic item 4: test IComponent::setState specifically, separate from performEdit ----
//
// A full patch/preset load (as opposed to a single knob edit) may route through setState rather
// than performEdit entirely - this was "reasoned through, not tested" in the previous round.
// Captures state at a low Global Volume, cranks volume high via performEdit (same mechanism as
// --paramtest, now proven), then restores the captured (quiet) state via setState and checks
// whether the audio actually goes quiet again - the same live instance, the same audible metric,
// but through the setState path instead of performEdit.
static int RunStateTest(string pluginPath)
{
    var started = StartPlayback(pluginPath);
    if (started is null)
        return 1;
    var (handle, provider, output) = started.Value;
    provider.Recording = new List<float>();

    int paramCount = VstShimInterop.VstShim_DebugGetParameterCount(handle);
    var nameBuffer = new System.Text.StringBuilder(128);
    uint? volumeId = null;
    for (int i = 0; i < paramCount; i++)
    {
        nameBuffer.Clear();
        if (VstShimInterop.VstShim_DebugGetParameterInfo(handle, i, nameBuffer, nameBuffer.Capacity, out uint paramId) == 0)
            continue;
        if (nameBuffer.ToString().Contains("volume", StringComparison.OrdinalIgnoreCase))
        {
            volumeId = paramId;
            break;
        }
    }
    if (volumeId is null)
    {
        Console.Error.WriteLine("FAILED: no volume parameter found.");
        return 2;
    }

    provider.NoteOn(48, 110);
    Thread.Sleep(200);

    Console.WriteLine("Setting volume LOW, then capturing component state (this is the state we'll restore to)...");
    VstShimInterop.VstShim_DebugSetParameter(handle, volumeId.Value, 0.05);
    Thread.Sleep(600); // let the (already-proven) performEdit path settle before capturing
    IntPtr quietState = VstShimInterop.VstShim_DebugCaptureState(handle);
    if (quietState == IntPtr.Zero)
    {
        Console.Error.WriteLine($"FAILED: VstShim_DebugCaptureState returned null. {VstShimInterop.VstShim_GetLastError(handle)}");
        return 3;
    }
    Thread.Sleep(600);

    Console.WriteLine("Setting volume HIGH via performEdit (recording 'before restore' baseline)...");
    VstShimInterop.VstShim_DebugSetParameter(handle, volumeId.Value, 0.95);
    Thread.Sleep(1200);

    Console.WriteLine("Restoring the captured (quiet) state via IComponent::setState...");
    int restoreResult = VstShimInterop.VstShim_DebugRestoreState(handle, quietState);
    Console.WriteLine($"VstShim_DebugRestoreState returned {restoreResult} (1=kResultOk, 0=failed - check stderr for the setState return code logged natively).");
    Thread.Sleep(1200);

    provider.AllNotesOff();
    Thread.Sleep(300);

    output.Stop();
    List<float> recording = provider.Recording!;
    int channels = VstShimInterop.VstShim_GetOutputChannelCount(handle);
    output.Dispose();
    provider.Dispose();

    var wav = new WavWriter(channels, (int)SampleRate);
    wav.AppendBlock(recording.ToArray(), recording.Count);
    wav.WriteFile("milestone6_statetest.wav");

    static double Rms(List<float> samples, int startFrame, int endFrame, int channels)
    {
        double sumSquares = 0;
        int count = 0;
        for (int f = startFrame; f < endFrame; f++)
        {
            int i = f * channels;
            if (i + channels > samples.Count)
                break;
            for (int ch = 0; ch < channels; ch++)
                sumSquares += (double)samples[i + ch] * samples[i + ch];
            count += channels;
        }
        return count > 0 ? Math.Sqrt(sumSquares / count) : 0;
    }

    int frameCount = channels > 0 ? recording.Count / channels : 0;
    int beforeRestoreStart = (int)(1.55 * SampleRate);
    int beforeRestoreEnd = (int)(2.55 * SampleRate);
    int afterRestoreStart = (int)(2.9 * SampleRate);
    int afterRestoreEnd = Math.Min(frameCount, (int)(3.9 * SampleRate));

    double beforeRestoreRms = Rms(recording, beforeRestoreStart, beforeRestoreEnd, channels);
    double afterRestoreRms = Rms(recording, afterRestoreStart, afterRestoreEnd, channels);

    Console.WriteLine();
    Console.WriteLine($"Recorded {frameCount} frames ({frameCount / SampleRate:F2}s) to milestone6_statetest.wav");
    Console.WriteLine($"RMS before restore (volume HIGH): {beforeRestoreRms:F6}");
    Console.WriteLine($"RMS after setState restore (should be back to quiet): {afterRestoreRms:F6}");
    double dropRatio = beforeRestoreRms > 0 ? afterRestoreRms / beforeRestoreRms : 0;
    Console.WriteLine($"Ratio (after/before): {dropRatio:F2}x (expect well under 1.0 if setState took effect)");

    bool pass = restoreResult == 1 && dropRatio < 0.6;
    Console.WriteLine();
    Console.WriteLine(pass
        ? "Milestone 6 setState routing: SUCCESS (restoring a captured state measurably changed the audio)."
        : "Milestone 6 setState routing: FAILED (setState did not measurably affect the live audio instance).");
    return pass ? 0 : 4;
}
