using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Microsoft.Win32;
using ToneDetector.Audio;
using ToneDetector.Core.Analysis;
using ToneDetector.Core.Pipeline;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>
/// Drives the file-analysis view: decodes a chosen audio file and runs it through the offline
/// pipeline (configured from the shared <see cref="PipelineSettingsViewModel"/>), or reads the
/// chosen parts of a MIDI file, then loads the notes into this tab's own editable
/// <see cref="Roll"/>. On load the Tempo box gets the detected tempo (the MIDI file's own tempo)
/// before notes turn into beats, so the bars follow the music; Play and Export use the roll.
/// </summary>
public sealed class FileAnalysisViewModel : INotifyPropertyChanged
{
    private const int WindowSize = 8192;
    private const int HopSize = 2048;

    private readonly PipelineSettingsViewModel _settings;
    private readonly AudioFileDecoder _decoder = new();

    /// <summary>This tab's piano roll: notes, tempo, grid, loop, playhead and edit history.</summary>
    public PianoRollViewModel Roll { get; }

    /// <summary>Set by the view: asks which parts of a MIDI file to load (null = cancelled).
    /// Without it, every part except drums is loaded.</summary>
    public Func<MidiFileContents, IReadOnlyList<MidiPart>?>? ChooseMidiParts { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<TranscriptEventRow> Rows { get; } = new();

    /// <summary>Results side panel rows for the selected track's notes (with Silence rows
    /// between), at the tab's tempo.</summary>
    public ObservableCollection<AnalysisResultRow> Results { get; } = new();

    public bool HasResults => Results.Count > 0;

    private bool _hideSilenceRows = true;
    /// <summary>Results leave out the Silence rows (on by default, like the Live Tuner's Grid).</summary>
    public bool HideSilenceRows
    {
        get => _hideSilenceRows;
        set
        {
            if (_hideSilenceRows == value)
                return;
            _hideSilenceRows = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HideSilenceRows)));
            RebuildResults();
        }
    }

    public string EmptyResultsText => HasFile ? "No notes in this track." : "Load a file to see its notes.";

    /// <summary>The selected track's notes (same collection as <see cref="Roll"/>'s).</summary>
    public ObservableCollection<PianoRollNote> RollNotes => Roll.RollNotes;

    private string? _loadedFileName;
    /// <summary>File name shown in the session bar, or null before the first file.</summary>
    public string? LoadedFileName
    {
        get => _loadedFileName;
        private set
        {
            SetField(ref _loadedFileName, value);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasFile)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EmptyResultsText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowStatusText)));
        }
    }

    public bool HasFile => LoadedFileName is not null;

    private void SetLoadFailed(bool failed)
    {
        _lastLoadFailed = failed;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowStatusText)));
    }

    private string _summaryText = "";
    /// <summary>"00:11.840 · 15 notes": length of the transcript and its note count.</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set => SetField(ref _summaryText, value);
    }

    /// <summary>Builds the session-bar summary for a transcript.</summary>
    internal static string Summarize(IReadOnlyList<TranscriptEvent> events)
    {
        double endMs = events.Count == 0 ? 0 : events.Max(e => e.StartTimeMs + e.DurationMs);
        int notes = events.Count(e => e.Type == TranscriptEventType.Note && e.Note is not null);
        return $"{TimeFormat.MillisecondsToDisplay(endMs)} · {notes} {(notes == 1 ? "note" : "notes")}";
    }

    /// <summary>Note events as piano-roll notes in beats at <paramref name="bpm"/> (Silence has no
    /// pitch to draw). Velocity follows confidence, as playback always did.</summary>
    internal static IEnumerable<PianoRollNote> ToRollNotes(IEnumerable<TranscriptEvent> events, double bpm) =>
        events.Where(e => e.Type == TranscriptEventType.Note && e.Note is not null)
              .Select(e => new PianoRollNote(
                  Guid.NewGuid(), e.Note!.Value.MidiNumber,
                  MusicalTime.MsToBeats(e.StartTimeMs, bpm), MusicalTime.MsToBeats(e.DurationMs, bpm),
                  Velocity: Math.Clamp((int)(32 + 95 * e.Note.Value.ConfidenceMean), 32, 127),
                  Confidence: e.Note.Value.ConfidenceMean, CentsDeviation: e.Note.Value.CentsDeviation,
                  MeanF0Hz: e.Note.Value.MeanF0Hz));

    /// <summary>Tempo for an analysed file: the tempo detected from its note onsets, or 120 when
    /// there are too few notes to tell.</summary>
    internal static double DetectTempo(IEnumerable<TranscriptEvent> events)
    {
        var detector = new TempoDetector();
        foreach (TranscriptEvent evt in events.Where(e => e.Type == TranscriptEventType.Note).OrderBy(e => e.StartTimeMs))
            detector.AddNoteOnset(evt.StartTimeMs);
        return detector.Detect() is { } tempo ? MusicalTime.ClampBpm(tempo.Bpm) : MusicalTime.DefaultBpm;
    }

    /// <summary>Picked MIDI parts as a transcript (for the Results panel), times at <paramref name="bpm"/>.
    /// MIDI notes are exact: 0 cents, equal-tempered frequency, confidence 1.</summary>
    internal static Transcript ToTranscript(IEnumerable<PianoRollNote> notes, double bpm, string sourceInfo) =>
        new(sourceInfo, notes
            .OrderBy(n => n.StartBeat).ThenBy(n => n.MidiNumber)
            .Select(n => new TranscriptEvent(
                TranscriptEventType.Note, MusicalTime.BeatsToMs(n.StartBeat, bpm), MusicalTime.BeatsToMs(n.DurationBeats, bpm),
                new NoteInfo(n.MidiNumber, 0.0, 440.0 * Math.Pow(2.0, (n.MidiNumber - 69) / 12.0), 1.0)))
            .ToList());

    private string _statusText = "No file loaded.";
    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    private bool _lastLoadFailed;
    /// <summary>The session bar shows <see cref="StatusText"/> instead of the file summary before
    /// the first file and after a failed load.</summary>
    public bool ShowStatusText => !HasFile || _lastLoadFailed;

    public RelayCommand LoadAndAnalyzeCommand { get; }
    public RelayCommand ExportJsonCommand { get; }
    public RelayCommand ExportCsvCommand { get; }
    public RelayCommand ExportMidiCommand { get; }

    public FileAnalysisViewModel(PipelineSettingsViewModel settings, PlaybackViewModel playback)
    {
        _settings = settings;
        Roll = new PianoRollViewModel(playback);
        Roll.SelectedNotesChanged += (_, _) => RebuildResults();
        Roll.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PianoRollViewModel.Bpm))
                RebuildResults();
        };
        LoadAndAnalyzeCommand = new RelayCommand(_ => LoadAndAnalyze());
        ExportJsonCommand = new RelayCommand(_ => ExportJson(), _ => Roll.HasAnyNotes);
        ExportCsvCommand = new RelayCommand(_ => ExportCsv(), _ => Roll.HasAnyNotes);
        ExportMidiCommand = new RelayCommand(_ => ExportMidi(), _ => Roll.HasAnyNotes);
    }

    private void RebuildResults()
    {
        Results.Clear();
        foreach (AnalysisResultRow row in AnalysisResultRow.Build(Roll.RollNotes, Roll.Bpm, HideSilenceRows))
            Results.Add(row);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasResults)));
    }

    /// <summary>Audio formats the decoder opens: WAV, AIFF and MP3 through NAudio's own readers,
    /// the rest through Windows Media Foundation (built into Windows 10/11; M4A covers AAC and
    /// Apple Lossless).</summary>
    internal static readonly string[] AudioExtensions =
        { ".wav", ".aiff", ".aif", ".mp3", ".m4a", ".aac", ".wma", ".flac" };

    /// <summary>Standard MIDI Files, read as notes rather than analysed as audio.</summary>
    internal static readonly string[] MidiExtensions = { ".mid", ".midi" };

    /// <summary>Open dialog filter: all supported files first, then audio, MIDI, and anything.</summary>
    internal static string OpenFileFilter
    {
        get
        {
            static string Patterns(IEnumerable<string> extensions) => string.Join(";", extensions.Select(e => "*" + e));
            string all = Patterns(AudioExtensions.Concat(MidiExtensions));
            string audio = Patterns(AudioExtensions);
            string midi = Patterns(MidiExtensions);
            return $"Audio and MIDI files|{all}|Audio files ({audio})|{audio}|MIDI files ({midi})|{midi}|All files (*.*)|*.*";
        }
    }

    internal static bool IsMidiFile(string filePath) =>
        MidiExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase);

    private void LoadAndAnalyze()
    {
        var dialog = new OpenFileDialog { Filter = OpenFileFilter };
        if (dialog.ShowDialog() != true)
            return;
        if (!Roll.ConfirmReplaceTracks())
            return;

        try
        {
            Transcript transcript;
            double bpm;
            List<TrackSeed> tracks;
            if (IsMidiFile(dialog.FileName))
            {
                StatusText = "Reading MIDI...";
                MidiFileContents contents = MidiImporter.Read(dialog.FileName);
                IReadOnlyList<MidiPart>? parts = contents.Parts.Count <= 1
                    ? contents.Parts
                    : ChooseMidiParts is { } choose ? choose(contents) : contents.Parts.Where(p => !p.IsDrums).ToList();
                if (parts is null)
                {
                    StatusText = LoadedFileName is null ? "No file loaded." : StatusText;
                    return;
                }
                bpm = contents.Bpm;
                // One track per part, in the file's order.
                tracks = parts.Select(p => new TrackSeed(p.Name, p.Notes, p.IsDrums)).ToList();
                transcript = ToTranscript(parts.SelectMany(p => p.Notes), bpm, dialog.FileName);
            }
            else
            {
                StatusText = "Decoding...";
                (float[] samples, int sampleRate) = _decoder.DecodeToMono(dialog.FileName);

                var pipeline = new PitchAnalysisPipeline();
                _settings.ApplyTo(pipeline, sampleRate);

                transcript = OfflineAnalyzer.Analyze(
                    samples, sampleRate, pipeline, WindowSize, HopSize, dialog.FileName,
                    _settings.MinSilenceMs, _settings.MaxVoicedGapMs);

                // Tempo first, so the notes land on the music's own bars.
                bpm = DetectTempo(transcript.Events);
                tracks = new List<TrackSeed>
                {
                    new(Path.GetFileNameWithoutExtension(dialog.FileName), ToRollNotes(transcript.Events, bpm).ToList()),
                };
            }

            Rows.Clear();
            foreach (TranscriptEvent evt in transcript.Events)
                Rows.Add(TranscriptEventRow.FromEvent(evt));
            Roll.Bpm = bpm;
            Roll.ReplaceTracks(tracks);
            LoadedFileName = Path.GetFileName(dialog.FileName);
            SummaryText = Summarize(transcript.Events);
            SetLoadFailed(false);

            StatusText = $"Analyzed {transcript.Events.Count} events from {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to analyze file: {ex.Message}";
            SetLoadFailed(true);
        }

        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    }

    private void ExportJson()
    {
        if (!Roll.HasAnyNotes)
            return;

        var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "transcript.json" };
        if (dialog.ShowDialog() == true)
            TranscriptExporter.ExportJson(Roll.ExportTracks(), dialog.FileName, Roll.Bpm);
    }

    private void ExportCsv()
    {
        if (!Roll.HasAnyNotes)
            return;

        var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv", FileName = "transcript.csv" };
        if (dialog.ShowDialog() == true)
            TranscriptExporter.ExportCsv(Roll.ExportTracks(), dialog.FileName, Roll.Bpm);
    }

    private void ExportMidi()
    {
        if (!Roll.HasAnyNotes)
            return;

        var dialog = new SaveFileDialog { Filter = "MIDI (*.mid)|*.mid", FileName = "transcript.mid" };
        if (dialog.ShowDialog() == true)
            MidiExporter.Export(Roll.ExportTracks(), dialog.FileName, Roll.Bpm);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value))
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
