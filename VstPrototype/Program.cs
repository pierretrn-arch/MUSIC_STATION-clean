// Stage 4a prototype (PHASE4-VST3.md). Only "What to prove" item 1 (plugin enumeration) is
// implemented here — items 2-6 all require loading and driving an actual VST3 plugin, which
// turned out to be blocked before any of that code could be written. See
// VST-PROTOTYPE-RESULTS.md for the full findings.

using System.IO;
using System.Linq;

string defaultPath = @"C:\Program Files\Common Files\VST3";
string scanPath = args.Length > 0 ? args[0] : defaultPath;

Console.WriteLine($"Scanning for VST3 plugins under: {scanPath}");

if (!Directory.Exists(scanPath))
{
    Console.WriteLine("Path does not exist.");
    return;
}

// A .vst3 "file" is actually a directory bundle on Windows (Contents/x86_64-win/*.vst3 holds the
// real native module), so look for directories named *.vst3 rather than files.
IEnumerable<string> bundles = Directory.EnumerateDirectories(scanPath, "*.vst3", SearchOption.AllDirectories);

int count = 0;
foreach (string bundle in bundles)
{
    count++;
    string name = Path.GetFileNameWithoutExtension(bundle);
    string? nativeModule = Directory.Exists(Path.Combine(bundle, "Contents", "x86_64-win"))
        ? Directory.EnumerateFiles(Path.Combine(bundle, "Contents", "x86_64-win"), "*.vst3").FirstOrDefault()
        : null;

    Console.WriteLine($"  [{count}] {name}");
    Console.WriteLine($"      Bundle: {bundle}");
    Console.WriteLine($"      Native module (x64): {nativeModule ?? "(not found)"}");
}

Console.WriteLine(count == 0 ? "No plugins found." : $"Found {count} plugin bundle(s).");
