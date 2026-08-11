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
}
