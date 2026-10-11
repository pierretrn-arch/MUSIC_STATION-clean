namespace ToneDetector.Core.Pitch;

/// <summary>
/// Converts between linear frequency (Hz), absolute pitch in cents (log-frequency space,
/// anchored so A4 = 440 Hz = 6900 cents = MIDI note 69), and the nearest musical note.
/// Pitch-track smoothing stages operate on the "absolute cents" representation produced here.
/// </summary>
public static class NoteQuantizer
{
    private const double A4Frequency = 440.0;
    private const int A4MidiNumber = 69;
    private static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

    public static double FrequencyToAbsoluteCents(double frequencyHz)
        => 1200.0 * Math.Log2(frequencyHz / A4Frequency) + A4MidiNumber * 100.0;

    public static double AbsoluteCentsToFrequency(double absoluteCents)
        => A4Frequency * Math.Pow(2.0, (absoluteCents - A4MidiNumber * 100.0) / 1200.0);

    /// <summary>Rounds absolute cents to the nearest semitone, returning the MIDI note number
    /// and the signed residual deviation in cents (-50..+50).</summary>
    public static (int MidiNote, double CentsDeviation) QuantizeToNearestNote(double absoluteCents)
    {
        int midiNote = (int)Math.Round(absoluteCents / 100.0, MidpointRounding.AwayFromZero);
        double centsDeviation = absoluteCents - midiNote * 100.0;
        return (midiNote, centsDeviation);
    }

    public static string MidiNoteName(int midiNote)
    {
        (string name, int octave) = GetNoteNameAndOctave(midiNote);
        return $"{name}{octave}";
    }

    /// <summary>Splits a MIDI note number into its note-class name (e.g. "C#") and octave (e.g. 4),
    /// for UI that displays them in separate columns.</summary>
    public static (string Name, int Octave) GetNoteNameAndOctave(int midiNote)
    {
        int octave = midiNote / 12 - 1;
        string name = NoteNames[((midiNote % 12) + 12) % 12];
        return (name, octave);
    }

    /// <summary>Name of a pitch class 0-11 (0=C, 1=C#, ... 11=B), independent of octave.</summary>
    public static string PitchClassName(int pitchClass) => NoteNames[((pitchClass % 12) + 12) % 12];

    /// <summary>Pitch class (0-11) of a MIDI note number.</summary>
    public static int GetPitchClass(int midiNote) => ((midiNote % 12) + 12) % 12;

    /// <summary>Parses a note-class name (e.g. "C", "c#", "Bb") into a pitch class 0-11 - used for
    /// committing a hand-typed Grid cell edit back to a MIDI number. Case-insensitive; accepts
    /// both "#"/"s" sharp and "b" flat spellings. Never throws - returns false on anything that
    /// doesn't match a known note name, consistent with this codebase's "never throw on bad user
    /// input" convention (e.g. SettingsStore).</summary>
    public static bool TryParseNoteName(string? text, out int pitchClass)
    {
        pitchClass = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string trimmed = text.Trim();
        int baseIndex = char.ToUpperInvariant(trimmed[0]) switch
        {
            'C' => 0,
            'D' => 2,
            'E' => 4,
            'F' => 5,
            'G' => 7,
            'A' => 9,
            'B' => 11,
            _ => -1,
        };
        if (baseIndex < 0)
            return false;

        int accidental = 0;
        for (int i = 1; i < trimmed.Length; i++)
        {
            switch (trimmed[i])
            {
                case '#' or 's' or 'S': accidental++; break;
                case 'b' or 'B': accidental--; break;
                default: return false; // unrecognized trailing character
            }
        }

        pitchClass = ((baseIndex + accidental) % 12 + 12) % 12;
        return true;
    }

    /// <summary>Combines a note-class name and octave into a MIDI note number - the inverse of
    /// <see cref="GetNoteNameAndOctave"/>, for committing hand-typed Grid Note/Octave cell edits.</summary>
    public static bool TryComputeMidiNumber(string? noteName, int octave, out int midiNumber)
    {
        midiNumber = 0;
        if (!TryParseNoteName(noteName, out int pitchClass))
            return false;
        midiNumber = pitchClass + (octave + 1) * 12;
        return true;
    }
}
