using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LabEquipmentController;

/// <summary>
/// The two pieces of text handling both script runners need: naming a captured reply and
/// splitting a comma-separated argument list.
///
/// Shared rather than copied because the single-instrument runner now records results the
/// same way the sequence runner does, and a script author moving between the two windows
/// should not discover that "$v" or "a, b" means something subtly different in one of them.
/// </summary>
internal static class ScriptValues
{
    /// <summary>Replace $name with the value captured or swept under that name.</summary>
    public static string Substitute(string text, Dictionary<string, string> vars)
    {
        if (text.IndexOf('$') < 0 || vars.Count == 0) return text;

        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '$') { sb.Append(text[i]); continue; }

            int j = i + 1;
            while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_')) j++;

            string name = text[(i + 1)..j];
            // An unknown name is left as written rather than blanked: "$5" in a comment or a
            // stray '$' should not silently turn a command into a different one.
            sb.Append(vars.TryGetValue(name, out string? v) && name.Length > 0 ? v : text[i..j]);
            i = j - 1;
        }
        return sb.ToString();
    }

    /// <summary>Comma-separated items, trimmed, with empties dropped.</summary>
    public static List<string> Split(string text)
        => text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// <summary>
    /// The fields of a RECORD line: split first, then substitute each one.
    /// </summary>
    /// <remarks>
    /// Order is the whole point. Substituting first and splitting afterwards lets a captured
    /// value's own commas become column boundaries, and instrument replies are full of
    /// commas — every *IDN? is four of them. "RECORD $v, $who" against a meter therefore
    /// produced five columns under a two-column heading, silently, and the CSV that came out
    /// was misaligned from the first row.
    ///
    /// Emptiness is treated as two different things. A field that is empty in the script —
    /// the tail of a trailing comma — is punctuation and is dropped. A field that is empty
    /// only *after* substitution is a reading that did not arrive, and it is kept, because
    /// dropping it slides every later value one column to the left and a gap becomes wrong
    /// data rather than missing data.
    /// </remarks>
    public static List<string> Fields(string text, Dictionary<string, string> vars)
    {
        var written = text.Split(',').Select(s => s.Trim()).ToList();
        while (written.Count > 0 && written[^1].Length == 0) written.RemoveAt(written.Count - 1);
        return written.Select(f => Substitute(f, vars)).ToList();
    }
}
