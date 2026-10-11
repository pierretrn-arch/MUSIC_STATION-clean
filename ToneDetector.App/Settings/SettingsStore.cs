using System.IO;
using System.Text.Json;

namespace ToneDetector.App.Settings;

/// <summary>
/// Reads/writes <see cref="AppSettings"/> as JSON in the current user's AppData folder, so Settings
/// tab changes (voicing sensitivity, filter cutoffs, etc.) survive an app restart instead of
/// silently resetting to defaults every launch.
/// </summary>
public static class SettingsStore
{
    private static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicStation", "settings.json");

    /// <summary>Where settings lived before the app was renamed from Tone Detector.</summary>
    private static readonly string LegacyFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ToneDetector", "settings.json");

    /// <summary>Never throws - a missing file (first run) or a corrupt/unreadable one (manual
    /// editing gone wrong, a future version's incompatible format) both just fall back to
    /// <see cref="AppSettings"/>'s own defaults rather than blocking startup. Copies the pre-rename
    /// settings file across first, if that's all there is.</summary>
    public static AppSettings Load()
    {
        MigrateLegacyFile(LegacyFilePath, DefaultFilePath);
        return Load(DefaultFilePath);
    }

    /// <summary>One-time carry-over after the rename: copies <paramref name="legacyPath"/> to
    /// <paramref name="currentPath"/> when only the legacy file exists. Never overwrites a current
    /// file, leaves the legacy file in place, and never throws (a failed copy just means default
    /// settings, as on a first run).</summary>
    internal static void MigrateLegacyFile(string legacyPath, string currentPath)
    {
        try
        {
            if (File.Exists(currentPath) || !File.Exists(legacyPath))
                return;

            string? dir = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.Copy(legacyPath, currentPath, overwrite: false);
        }
        catch
        {
            // Best-effort - see doc comment above.
        }
    }

    /// <summary>Never throws - a save failure (locked file, read-only AppData, disk full) shouldn't
    /// prevent the app from closing; the user just loses this session's setting changes, the same
    /// failure mode as if persistence didn't exist at all.</summary>
    public static void Save(AppSettings settings) => Save(settings, DefaultFilePath);

    /// <summary>Path-parameterized overload so tests can round-trip against a temp file instead of
    /// the real user's AppData settings.</summary>
    internal static AppSettings Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
                return new AppSettings();

            string json = File.ReadAllText(filePath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    internal static void Save(AppSettings settings, string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch
        {
            // Best-effort - see doc comment above.
        }
    }
}
