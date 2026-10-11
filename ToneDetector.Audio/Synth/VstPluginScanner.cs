namespace ToneDetector.Audio.Synth;

/// <summary>One discovered VST3 plugin: display name plus the path <see cref="VstHostSynth"/>'s
/// constructor (and <c>VstShim_LoadPlugin</c> underneath it) expects. On Windows a VST3 plugin is
/// usually a bundle directory ending in ".vst3", but older plugins ship as a single ".vst3" file
/// (a renamed DLL) - the VST3 SDK's own <c>Module::create</c> handles both transparently based on
/// whether the path is a directory or a file, so both forms are valid here.</summary>
public sealed record VstPluginInfo(string Name, string Path);

/// <summary>
/// Finds installed VST3 plugins by scanning the standard install location plus any
/// user-configured extra folders (PHASE4-VST3-NATIVE.md Milestone 7 - some plugins install
/// outside the default Program Files path). Pure filesystem logic with no native/plugin
/// dependency, so it's fully unit-testable against a fake directory structure.
/// </summary>
public static class VstPluginScanner
{
    public const string DefaultScanPath = @"C:\Program Files\Common Files\VST3";

    /// <summary>Scans every given folder (recursively) for ".vst3" bundle directories and
    /// single-file ".vst3" plugins. Missing or inaccessible folders are skipped silently rather
    /// than throwing - a stale/mistyped additional scan path shouldn't prevent finding plugins
    /// under the default one.</summary>
    public static IReadOnlyList<VstPluginInfo> Scan(IEnumerable<string> scanPaths)
    {
        var results = new List<VstPluginInfo>();
        foreach (string root in scanPaths)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            List<string> bundles;
            List<string> singleFiles;
            try
            {
                // .ToList() forces eager enumeration inside the try - Enumerate*() is lazy, so a
                // permission error on a subfolder would otherwise surface later, outside this
                // catch, the first time the (deferred) sequence is actually iterated.
                bundles = Directory.EnumerateDirectories(root, "*.vst3", SearchOption.AllDirectories).ToList();

                // A bundle's own Contents/<arch>/ subfolder contains a same-named ".vst3" file
                // (e.g. "Surge XT.vst3/Contents/x86_64-win/Surge XT.vst3") - that's the bundle's
                // internal binary, not a second, separate single-file plugin, so anything found
                // under a directory that's already a matched bundle must be excluded here or it'd
                // be listed twice.
                singleFiles = Directory.EnumerateFiles(root, "*.vst3", SearchOption.AllDirectories)
                    .Where(file => !bundles.Any(bundle =>
                        file.StartsWith(bundle + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                continue; // a permission-denied subfolder shouldn't abort the whole scan
            }

            foreach (string pluginPath in bundles.Concat(singleFiles))
                results.Add(new VstPluginInfo(System.IO.Path.GetFileNameWithoutExtension(pluginPath), pluginPath));
        }
        return results;
    }
}
