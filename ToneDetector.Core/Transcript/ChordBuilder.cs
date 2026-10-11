namespace ToneDetector.Core.Transcript;

/// <summary>
/// Pure semitone-offset math for turning a single root pitch into a chord voicing. No wrapping/
/// modulo is applied to the result — offsets are added directly to the root MIDI note number, so
/// e.g. a root near the top of the MIDI range plus a wide voicing (Augmented, 7ths) simply
/// produces higher note numbers rather than silently folding back into a lower octave.
/// </summary>
public static class ChordBuilder
{
    private static readonly IReadOnlyDictionary<ChordQuality, int[]> Offsets = new Dictionary<ChordQuality, int[]>
    {
        [ChordQuality.Major] = new[] { 0, 4, 7 },
        [ChordQuality.Minor] = new[] { 0, 3, 7 },
        [ChordQuality.Power] = new[] { 0, 7 },
        [ChordQuality.Diminished] = new[] { 0, 3, 6 },
        [ChordQuality.Augmented] = new[] { 0, 4, 8 },
        [ChordQuality.Sus2] = new[] { 0, 2, 7 },
        [ChordQuality.Sus4] = new[] { 0, 5, 7 },
        [ChordQuality.Dominant7th] = new[] { 0, 4, 7, 10 },
        [ChordQuality.Major7th] = new[] { 0, 4, 7, 11 },
        [ChordQuality.Minor7th] = new[] { 0, 3, 7, 10 },
    };

    /// <summary>"Major", "Dominant 7th"...</summary>
    public static string QualityName(ChordQuality quality) => quality switch
    {
        ChordQuality.Power => "Power",
        ChordQuality.Dominant7th => "Dominant 7th",
        ChordQuality.Major7th => "Major 7th",
        ChordQuality.Minor7th => "Minor 7th",
        _ => quality.ToString(),
    };

    /// <summary>The chord's name for the Grid: root pitch class and quality, e.g. "C# Minor".</summary>
    public static string ChordName(int rootMidiNote, ChordQuality quality) =>
        $"{Pitch.NoteQuantizer.PitchClassName(Pitch.NoteQuantizer.GetPitchClass(rootMidiNote))} {QualityName(quality)}";

    public static int[] BuildChord(int rootMidiNote, ChordQuality quality)
    {
        int[] offsets = Offsets[quality];
        var notes = new int[offsets.Length];
        for (int i = 0; i < offsets.Length; i++)
            notes[i] = rootMidiNote + offsets[i];
        return notes;
    }
}
