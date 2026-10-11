using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

namespace ToneDetector.Core.Diagnostics;

/// <summary>
/// Per-frame diagnostic trace for the voicing gate (DETECTION-DIAGNOSTICS-PRESETS.md Change 10) -
/// evidence for tuning <see cref="Pitch.YinPitchDetector.RmsSilenceThreshold"/>/
/// <see cref="Pitch.YinPitchDetector.AperiodicityThreshold"/> against real input instead of
/// guessing. Toggleable, off by default, meant for short deliberate debugging sessions rather than
/// continuous use.
/// <para/>
/// <see cref="LogFrame"/> is called from the real-time audio-capture thread (see
/// <c>LiveTunerViewModel.OnFrameReady</c>) once per analysis frame, so it must never block on I/O
/// there - it only formats a line and enqueues it. A background <see cref="Flush"/> (driven by the
/// owner, e.g. a timer) does the actual file write.
/// </summary>
public sealed class VoicingDiagnosticsLogger
{
    private readonly ConcurrentQueue<string> _pending = new();
    private readonly string _filePath;

    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MusicStation", "voicing-diagnostics.log");

    public VoicingDiagnosticsLogger(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    /// <summary>Formats and enqueues one frame's measurements. Cheap and allocation-light enough
    /// to call unconditionally from the audio thread every frame while logging is enabled; callers
    /// should still skip calling this entirely while logging is disabled, to avoid even the
    /// formatting cost.
    /// <para/>
    /// <paramref name="aperiodicity"/> is only a genuine YIN measurement when
    /// <paramref name="rms"/> already cleared <paramref name="minSignalLevel"/> -
    /// <see cref="Pitch.YinPitchDetector.DetectPitch"/> returns early on a silent frame without
    /// ever running the pitch search, reporting a fixed placeholder aperiodicity of 1.0 in that
    /// case. Logged explicitly as "not computed" rather than as a real 1.0 measurement, since
    /// misreading a placeholder as "this frame was maximally aperiodic" would be exactly the kind
    /// of misleading evidence Change 10 exists to avoid.</summary>
    public void LogFrame(double rms, double aperiodicity, double minSignalLevel, double breathinessTolerance, bool isVoiced)
    {
        // Explicit InvariantCulture throughout - the exact same class of bug just found and fixed
        // in the Settings decimal-typing investigation (this machine's culture formats "0.5" as
        // "0,5"), except here it would silently corrupt the log's readability/parseability rather
        // than fight a TextBox. A diagnostic log meant to be read (and possibly grepped/parsed)
        // needs a fixed, predictable number format regardless of the machine's locale.
        bool rejectedBySignalLevel = rms < minSignalLevel;
        string aperiodicityText = rejectedBySignalLevel
            ? "n/a (rejected by Min. signal level before pitch search ran)"
            : aperiodicity.ToString("F5", CultureInfo.InvariantCulture);
        string reason = isVoiced
            ? "accepted"
            : rejectedBySignalLevel
                ? "rejected: below Min. signal level"
                : "rejected: above Breathiness tolerance";

        string timestamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string rmsText = rms.ToString("F5", CultureInfo.InvariantCulture);
        string minSignalLevelText = minSignalLevel.ToString("F5", CultureInfo.InvariantCulture);
        string breathinessToleranceText = breathinessTolerance.ToString("F5", CultureInfo.InvariantCulture);

        _pending.Enqueue(
            $"{timestamp}  rms={rmsText} (Min. signal level={minSignalLevelText})  " +
            $"aperiodicity={aperiodicityText} (Breathiness tolerance={breathinessToleranceText})  " +
            $"voiced={isVoiced}  [{reason}]");
    }

    /// <summary>Truncates the log file and writes a session-start banner - call when logging is
    /// switched on, so a new debugging session isn't buried at the bottom of a stale file from a
    /// previous one. Safe to call from any thread; never throws.</summary>
    public void StartNewSession()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            File.WriteAllText(_filePath, $"=== Voicing diagnostics started {timestamp} ==={Environment.NewLine}");
        }
        catch
        {
            // Best-effort - a logging failure shouldn't interrupt real-time audio processing.
        }
    }

    /// <summary>Appends every line enqueued since the last flush to the log file in one write.
    /// Never throws. Safe to call from a background timer, or directly (e.g. from tests) for
    /// deterministic assertions without waiting on one.</summary>
    public void Flush()
    {
        if (_pending.IsEmpty)
            return;

        var sb = new StringBuilder();
        while (_pending.TryDequeue(out string? line))
            sb.AppendLine(line);

        try
        {
            File.AppendAllText(_filePath, sb.ToString());
        }
        catch
        {
            // Best-effort - see doc comment above. Dropped lines are an acceptable loss for a
            // best-effort debugging aid; blocking/crashing real-time audio processing is not.
        }
    }
}
