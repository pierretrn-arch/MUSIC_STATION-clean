using System.IO;
using ToneDetector.App.Settings;
using Xunit;

namespace ToneDetector.Tests;

/// <summary>Tests <see cref="SettingsStore.MigrateLegacyFile"/>, the one-time copy of settings from
/// %AppData%\ToneDetector to %AppData%\MusicStation after the rename, against a temp folder.</summary>
public sealed class SettingsStoreMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "SettingsStoreMigrationTests_" + Guid.NewGuid());
    private string LegacyPath => Path.Combine(_root, "ToneDetector", "settings.json");
    private string CurrentPath => Path.Combine(_root, "MusicStation", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static void WriteSettings(string path, double highPassCutoffHz)
    {
        SettingsStore.Save(new AppSettings { HighPassCutoffHz = highPassCutoffHz }, path);
    }

    [Fact]
    public void OnlyLegacyFileExists_CopiesItAcross_AndKeepsTheOriginal()
    {
        WriteSettings(LegacyPath, 123.0);

        SettingsStore.MigrateLegacyFile(LegacyPath, CurrentPath);

        Assert.True(File.Exists(CurrentPath));
        Assert.True(File.Exists(LegacyPath));
        Assert.Equal(123.0, SettingsStore.Load(CurrentPath).HighPassCutoffHz);
    }

    [Fact]
    public void CurrentFileExists_IsNeverOverwritten()
    {
        WriteSettings(LegacyPath, 123.0);
        WriteSettings(CurrentPath, 456.0);

        SettingsStore.MigrateLegacyFile(LegacyPath, CurrentPath);

        Assert.Equal(456.0, SettingsStore.Load(CurrentPath).HighPassCutoffHz);
    }

    [Fact]
    public void NoLegacyFile_DoesNothing()
    {
        SettingsStore.MigrateLegacyFile(LegacyPath, CurrentPath);

        Assert.False(File.Exists(CurrentPath));
    }

    [Fact]
    public void LegacyPathThatIsNotAFile_IsIgnored_WithoutThrowing()
    {
        Directory.CreateDirectory(LegacyPath); // a folder, not a settings file

        SettingsStore.MigrateLegacyFile(LegacyPath, CurrentPath);

        Assert.False(File.Exists(CurrentPath));
    }
}
