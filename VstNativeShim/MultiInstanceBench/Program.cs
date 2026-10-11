using System.Diagnostics;
using System.Globalization;
using ToneDetector.Audio.Synth;

// Phase 6b Stage 2: how many copies of a VST3 instrument can one audio thread carry?
//
// Run from the repo root on Windows (Surge XT installed, VstShim.dll built as for the app):
//   dotnet run -c Release --project VstNativeShim\MultiInstanceBench
// Optional arguments: [path to .vst3] [max copies, default 8] [seconds of audio per step, default 10]
//
// For N = 1..maxCopies it loads N copies, holds a 4-note chord on each (a busy track), and renders
// `seconds` of audio per copy as fast as possible on ONE thread - the same way the app's mixer pulls
// every track on NAudio's single playback thread. It then reports:
//   Load %     time spent rendering / length of audio rendered. 100% = the audio thread has no
//              spare time at all (dropouts). This is the number the warning limit should use.
//   CPU %      process CPU time over the same span, as a share of ONE core (can exceed Load %
//              if the plugin runs its own worker threads).
//   Idle %     the same Load % with every note released (tracks that are set up but silent).
//   RAM MB     process working set with N copies loaded.
// Results also go to multi-instance-results.csv next to the exe.

const int SampleRate = 48000;
const int BlockFrames = 512;

string plugin = args.Length > 0 ? args[0] : @"C:\Program Files\Common Files\VST3\Surge Synth Team\Surge XT.vst3";
int maxCopies = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 8;
double seconds = args.Length > 2 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 10;

Console.WriteLine($"Plugin: {plugin}");
Console.WriteLine($"Copies: 1..{maxCopies}, {seconds:0.#} s of audio per copy per step, {SampleRate} Hz, {BlockFrames}-frame blocks");
Console.WriteLine($"Machine: {Environment.ProcessorCount} logical processors, {Environment.OSVersion}");
Console.WriteLine();
Console.WriteLine(" Copies   Load %   CPU %   Idle %   RAM MB");

var synths = new List<VstHostSynth>();
var rows = new List<string> { "copies,load_percent,cpu_percent,idle_load_percent,ram_mb" };
int[] chord = { 48, 55, 60, 64 };

try
{
    for (int n = 1; n <= maxCopies; n++)
    {
        synths.Add(new VstHostSynth(plugin));

        foreach (VstHostSynth synth in synths)
        {
            synth.AllNotesOff();
            foreach (int note in chord)
                synth.NoteOn(note, 100);
        }
        Render(synths, 0.5); // warm up: attacks, allocations
        (double load, double cpu) = Render(synths, seconds);

        foreach (VstHostSynth synth in synths)
            synth.AllNotesOff();
        Render(synths, 2.0); // let release tails finish
        (double idle, _) = Render(synths, Math.Min(seconds, 5));

        double ramMb = Process.GetCurrentProcess().WorkingSet64 / (1024.0 * 1024.0);
        Console.WriteLine($" {n,6}   {load,6:0.0}   {cpu,5:0.0}   {idle,6:0.0}   {ramMb,6:0}");
        rows.Add(string.Join(",", n, F(load), F(cpu), F(idle), F(ramMb)));
    }
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine($"Stopped at {synths.Count + 1} copies: {ex.Message}");
}

string csv = Path.Combine(AppContext.BaseDirectory, "multi-instance-results.csv");
File.WriteAllLines(csv, rows);
Console.WriteLine();
Console.WriteLine($"Saved {csv}");
Console.WriteLine("Unloading plugins (if this step crashes, the numbers above are still valid - please mention it)...");

for (int i = synths.Count - 1; i >= 0; i--)
    synths[i].Dispose();
Console.WriteLine("Done.");
return;

static string F(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);

// Renders `audioSeconds` of audio from every synth, block by block on this one thread.
static (double LoadPercent, double CpuPercent) Render(List<VstHostSynth> synths, double audioSeconds)
{
    int blocks = (int)Math.Ceiling(audioSeconds * SampleRate / BlockFrames);
    var buffers = synths.Select(s => new float[BlockFrames * s.WaveFormat.Channels]).ToArray();
    Process process = Process.GetCurrentProcess();
    process.Refresh();
    TimeSpan cpuBefore = process.TotalProcessorTime;
    var clock = Stopwatch.StartNew();

    for (int b = 0; b < blocks; b++)
    {
        for (int i = 0; i < synths.Count; i++)
            synths[i].Read(buffers[i], 0, buffers[i].Length);
    }

    clock.Stop();
    process.Refresh();
    double renderedSeconds = blocks * (double)BlockFrames / SampleRate;
    double load = clock.Elapsed.TotalSeconds / renderedSeconds * 100.0;
    double cpu = (process.TotalProcessorTime - cpuBefore).TotalSeconds / renderedSeconds * 100.0;
    return (load, cpu);
}
