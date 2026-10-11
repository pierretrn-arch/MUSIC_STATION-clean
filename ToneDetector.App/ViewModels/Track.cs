using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using ToneDetector.Core.Transcript;

namespace ToneDetector.App.ViewModels;

/// <summary>
/// One track in a tab: its notes (in beats), name, colour (track-1 ... track-8), instrument and
/// mixer settings. The piano roll edits the selected track; the others show as ghost notes.
/// Name, colour and instrument change through <see cref="PianoRollViewModel"/> so they can be
/// undone; mute, solo, volume and pan are set directly (not in Undo, like most DAWs).
/// </summary>
public sealed class Track : INotifyPropertyChanged
{
    public const double DefaultVolume = MixerMath.UnityPosition;

    public Track(string name, int colourIndex, bool isDrums = false)
    {
        _name = name;
        _colourIndex = TrackPalette.IndexFor(colourIndex);
        IsDrums = isDrums;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; } = Guid.NewGuid();

    public ObservableCollection<PianoRollNote> Notes { get; } = new();

    /// <summary>A drum track (MIDI channel 10): drum kits only, drum view (Phase 6b Stage 3).</summary>
    public bool IsDrums { get; }

    private string _name;
    public string Name
    {
        get => _name;
        internal set => SetField(ref _name, value);
    }

    private int _colourIndex;
    /// <summary>0-7: track-1 ... track-8.</summary>
    public int ColourIndex
    {
        get => _colourIndex;
        internal set
        {
            if (SetField(ref _colourIndex, TrackPalette.IndexFor(value)))
            {
                OnPropertyChanged(nameof(Colour));
                OnPropertyChanged(nameof(ColourBrush));
            }
        }
    }

    public Color Colour => ColourOf(ColourIndex);

    public Brush ColourBrush => BrushOf(ColourIndex);

    private string _instrument = "Built-in synth";
    /// <summary>What the track plays through (the Settings synth until per-track instruments, Stage 2).</summary>
    public string Instrument
    {
        get => _instrument;
        internal set
        {
            if (SetField(ref _instrument, value))
                OnPropertyChanged(nameof(InstrumentChoices));
        }
    }

    private bool _isSelected;
    /// <summary>The track the roll edits (set by <see cref="PianoRollViewModel.SelectedTrack"/>).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        internal set => SetField(ref _isSelected, value);
    }

    /// <summary>The instrument picker's choices: just the current engine until per-track
    /// instruments (Phase 6b Stage 2).</summary>
    public IReadOnlyList<string> InstrumentChoices => new[] { Instrument };

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set => SetField(ref _isMuted, value);
    }

    private bool _isSolo;
    public bool IsSolo
    {
        get => _isSolo;
        set => SetField(ref _isSolo, value);
    }

    private double _volume = DefaultVolume;
    /// <summary>Fader position 0-1 (0.8 = 0 dB, 1 = +6 dB, 0 = silent).</summary>
    public double Volume
    {
        get => _volume;
        set
        {
            if (SetField(ref _volume, Math.Clamp(value, 0.0, 1.0)))
                OnPropertyChanged(nameof(DbText));
        }
    }

    /// <summary>"0.0", "−4.5", "−∞".</summary>
    public string DbText => MixerMath.DbText(Volume);

    public double Gain => MixerMath.PositionToGain(Volume);

    private int _pan;
    /// <summary>-100 (left) to +100 (right).</summary>
    public int Pan
    {
        get => _pan;
        set
        {
            if (SetField(ref _pan, MixerMath.ClampPan(value)))
                OnPropertyChanged(nameof(PanText));
        }
    }

    /// <summary>"C", "L20", "R15".</summary>
    public string PanText => MixerMath.PanText(Pan);

    public TrackExport ToExport() =>
        new(Name, TrackPalette.Colours[ColourIndex], Instrument, IsDrums, IsMuted, IsSolo, Volume, Pan, Notes.ToList());

    private static readonly Brush[] Brushes = TrackPalette.Colours.Select(hex =>
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        brush.Freeze();
        return (Brush)brush;
    }).ToArray();

    public static Color ColourOf(int index) => ((SolidColorBrush)BrushOf(index)).Color;

    public static Brush BrushOf(int index) => Brushes[TrackPalette.IndexFor(index)];

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName!);
        return true;
    }

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>What a newly loaded track holds.</summary>
public sealed record TrackSeed(string Name, IReadOnlyList<PianoRollNote> Notes, bool IsDrums = false);
