using System.IO;
using ToneDetector.Audio.Synth;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Tests <see cref="VstPluginScanner"/> against a real (temporary) directory structure -
/// pure filesystem logic, no native plugin or VstShim.dll dependency needed, per
/// PHASE4-VST3-NATIVE.md's explicit testing scope for this class.</summary>
public sealed class VstPluginScannerTests : IDisposable
{
    private readonly string _tempRoot;

    public VstPluginScannerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "VstPluginScannerTests_" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void Scan_FindsAVst3BundleDirectory_AndIgnoresNonBundleEntries()
    {
        string vendorDir = Path.Combine(_tempRoot, "Some Vendor");
        Directory.CreateDirectory(vendorDir);
        Directory.CreateDirectory(Path.Combine(vendorDir, "Cool Synth.vst3"));
        Directory.CreateDirectory(Path.Combine(vendorDir, "Not A Plugin")); // no .vst3 extension
        File.WriteAllText(Path.Combine(vendorDir, "readme.txt"), "not a plugin"); // plain file

        IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { _tempRoot });

        VstPluginInfo plugin = Assert.Single(results);
        Assert.Equal("Cool Synth", plugin.Name);
        Assert.Equal(Path.Combine(vendorDir, "Cool Synth.vst3"), plugin.Path);
    }

    [Fact]
    public void Scan_FindsMultipleBundlesAcrossVendorSubfolders()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Vendor A", "Synth One.vst3"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Vendor B", "Synth Two.vst3"));

        IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { _tempRoot });

        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Name == "Synth One");
        Assert.Contains(results, p => p.Name == "Synth Two");
    }

    [Fact]
    public void Scan_NonExistentPath_ReturnsEmptyWithoutThrowing()
    {
        IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { Path.Combine(_tempRoot, "does-not-exist") });

        Assert.Empty(results);
    }

    [Fact]
    public void Scan_CombinesResultsAcrossMultipleSearchPaths()
    {
        string secondRoot = Path.Combine(Path.GetTempPath(), "VstPluginScannerTests_second_" + Guid.NewGuid());
        Directory.CreateDirectory(secondRoot);
        try
        {
            Directory.CreateDirectory(Path.Combine(_tempRoot, "Default Location", "Plugin A.vst3"));
            Directory.CreateDirectory(Path.Combine(secondRoot, "Plugin B.vst3"));

            IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { _tempRoot, secondRoot });

            Assert.Equal(2, results.Count);
            Assert.Contains(results, p => p.Name == "Plugin A");
            Assert.Contains(results, p => p.Name == "Plugin B");
        }
        finally
        {
            Directory.Delete(secondRoot, recursive: true);
        }
    }

    [Fact]
    public void Scan_IgnoresBlankOrWhitespaceSearchPaths_WithoutThrowing()
    {
        IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { "", "   ", _tempRoot });

        Assert.Empty(results); // _tempRoot has no .vst3 bundles for this test
    }

    [Fact]
    public void Scan_EmptyDirectory_ReturnsEmpty()
    {
        IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { _tempRoot });

        Assert.Empty(results);
    }

    [Fact]
    public void Scan_FindsASingleFileVst3Plugin()
    {
        // Older plugins (e.g. iZotope BreakTweaker) ship as a plain ".vst3" file - a renamed DLL,
        // not a bundle directory. Module::create on the native side already handles this (branches
        // on is_directory vs. plain LoadLibraryW), so the scanner just needs to find it too.
        string vendorDir = Path.Combine(_tempRoot, "Some Vendor");
        Directory.CreateDirectory(vendorDir);
        File.WriteAllBytes(Path.Combine(vendorDir, "Old Plugin.vst3"), new byte[] { 0 });

        IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { _tempRoot });

        VstPluginInfo plugin = Assert.Single(results);
        Assert.Equal("Old Plugin", plugin.Name);
        Assert.Equal(Path.Combine(vendorDir, "Old Plugin.vst3"), plugin.Path);
    }

    [Fact]
    public void Scan_BundleDirectory_DoesNotAlsoReportItsInternalArchSpecificFile()
    {
        // A real bundle's Contents/<arch>/ subfolder contains a same-named ".vst3" file (e.g.
        // "Cool Synth.vst3/Contents/x86_64-win/Cool Synth.vst3", the bundle's actual binary) -
        // that must not be double-counted as a second, separate single-file plugin.
        string bundleDir = Path.Combine(_tempRoot, "Some Vendor", "Cool Synth.vst3");
        string archDir = Path.Combine(bundleDir, "Contents", "x86_64-win");
        Directory.CreateDirectory(archDir);
        File.WriteAllBytes(Path.Combine(archDir, "Cool Synth.vst3"), new byte[] { 0 });

        IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { _tempRoot });

        VstPluginInfo plugin = Assert.Single(results);
        Assert.Equal(bundleDir, plugin.Path);
    }

    [Fact]
    public void Scan_FindsBothBundleAndSingleFilePlugins_InTheSameFolder()
    {
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Vendor A", "Bundle Synth.vst3"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "Vendor B"));
        File.WriteAllBytes(Path.Combine(_tempRoot, "Vendor B", "Single File Synth.vst3"), new byte[] { 0 });

        IReadOnlyList<VstPluginInfo> results = VstPluginScanner.Scan(new[] { _tempRoot });

        Assert.Equal(2, results.Count);
        Assert.Contains(results, p => p.Name == "Bundle Synth");
        Assert.Contains(results, p => p.Name == "Single File Synth");
    }
}
