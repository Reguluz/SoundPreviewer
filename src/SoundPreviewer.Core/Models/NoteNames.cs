using System.Globalization;

namespace SoundPreviewer.Core.Models;

public static class NoteNames
{
    private static readonly string[] PitchClasses = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    public static string Format(int midiNote)
    {
        if (midiNote is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(midiNote));
        return PitchClasses[midiNote % 12] + (midiNote / 12 - 1).ToString(CultureInfo.InvariantCulture);
    }

    public static int Parse(string name) => TryParse(name, out int midiNote) ? midiNote :
        throw new ArgumentException("音名须为 C4、C#4 或 Db4 等科学音高记法，范围为 C-1 到 G9。", nameof(name));

    public static bool TryParse(string? name, out int midiNote)
    {
        midiNote = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        ReadOnlySpan<char> text = name.Trim();
        int pitchClass = char.ToUpperInvariant(text[0]) switch
        {
            'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => -1
        };
        if (pitchClass < 0 || text.Length < 2) return false;
        int position = 1;
        if (text[position] is '#' or '♯') { pitchClass++; position++; }
        else if (text[position] is 'b' or 'B' or '♭') { pitchClass--; position++; }
        if (!int.TryParse(text[position..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int octave) ||
            octave is < -2 or > 10) return false;
        int parsed = (octave + 1) * 12 + pitchClass;
        if (parsed is < 0 or > 127) return false;
        midiNote = parsed;
        return true;
    }
}
