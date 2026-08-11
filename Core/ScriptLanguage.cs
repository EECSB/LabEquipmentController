using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace LabEquipmentController;

/// <summary>What a run of characters in a script line is, for colouring.</summary>
public enum ScriptTokenKind
{
    Plain,
    Comment,
    Keyword,
    /// <summary>The <c>gen:</c> at the head of a line, or the alias after WITH/DEVICE.</summary>
    Alias,
    /// <summary>A <c>$name</c> substitution, or the name after <c>-&gt;</c>.</summary>
    Variable,
    Number,
    /// <summary>The SCPI itself — left plain-ish on purpose; it is the point of the line.</summary>
    Command,
    /// <summary>Punctuation that carries meaning: <c>-&gt;</c>, the <c>:</c> after an alias.</summary>
    Operator,
}

/// <param name="Start">Index into the line.</param>
public sealed record ScriptToken(int Start, int Length, ScriptTokenKind Kind);

/// <summary>
/// A ready-made piece of script, offered in the Snippets list and expanded by Tab.
/// </summary>
/// <param name="Trigger">
/// What the user types before pressing Tab. Lower-case; matching is case-insensitive.
/// </param>
/// <param name="Title">How it reads in the dropdown.</param>
/// <param name="Summary">One line saying what it is for. Shown beside the title.</param>
/// <param name="Body">
/// The text inserted. Placeholders are written «like this»: after insertion the first is
/// selected and Tab moves to the next, which is the behaviour anyone who has used an IDE
/// expects. <c>\n</c> here; the editor converts to the line ending it needs.
/// </param>
public sealed record ScriptSnippet(string Trigger, string Title, string Summary, string Body)
{
    /// <summary>The placeholder marks, as the editor scans for them.</summary>
    public const char PlaceholderOpen = '«';
    public const char PlaceholderClose = '»';

    /// <summary>Where the placeholders are, in the order Tab should visit them.</summary>
    public static IReadOnlyList<(int Start, int Length)> PlaceholdersIn(string text)
    {
        var found = new List<(int, int)>();
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != PlaceholderOpen) continue;
            int close = text.IndexOf(PlaceholderClose, i + 1);
            if (close < 0) break;
            found.Add((i, close - i + 1));
            i = close;
        }
        return found;
    }
}

/// <summary>
/// The script language itself: what its words are, what they mean, and how a line breaks
/// into coloured pieces.
///
/// It is worth saying plainly that this language is ours. The commands inside it are SCPI,
/// which is a real standard; everything around them — DEVICE, WITH, FOR…POINTS…LOG, RECORD,
/// <c>-&gt;</c>, <c>$name</c> — was invented for this app and exists nowhere else. Nobody has
/// seen it before, which is why the editor has to teach it: a snippet list you can read, a
/// completion popup that offers the next word, and colour that shows the shape of a line.
///
/// Two dialects, because there are two editors. <see cref="ForScript"/> drives one
/// instrument and its lines carry no prefix; <see cref="ForSequence"/> adds the words that
/// only mean anything when several instruments are in play.
/// </summary>
public sealed class ScriptLanguage
{
    /// <summary>Words that begin a line and mean something to the runner.</summary>
    public IReadOnlyList<string> Keywords { get; }

    /// <summary>Words that appear inside a FOR line rather than at the head of one.</summary>
    public IReadOnlyList<string> InnerKeywords { get; }

    public IReadOnlyList<ScriptSnippet> Snippets { get; }

    /// <summary>True for the multi-instrument dialect, where a line may be addressed.</summary>
    public bool IsSequence { get; }

    private readonly HashSet<string> _allWords;

    private ScriptLanguage(bool isSequence, IReadOnlyList<string> keywords,
                           IReadOnlyList<string> inner, IReadOnlyList<ScriptSnippet> snippets)
    {
        IsSequence = isSequence;
        Keywords = keywords;
        InnerKeywords = inner;
        Snippets = snippets;
        _allWords = new HashSet<string>(keywords.Concat(inner), StringComparer.OrdinalIgnoreCase);
    }

    public bool IsKeyword(string word) => _allWords.Contains(word);

    // ------------------------------------------------------------------------ dialects

    private static readonly string[] ScriptKeywords =
        { "DELAY", "WAIT", "PRINT", "ECHO", "LOG", "REPEAT", "END" };

    private static readonly string[] SequenceKeywords =
        { "DEVICE", "WITH", "FOR", "RECORD", "COLUMNS",
          "DELAY", "WAIT", "PRINT", "ECHO", "LOG", "REPEAT", "END" };

    private static readonly string[] SequenceInner = { "TO", "STEP", "POINTS", "LOG" };

    private static readonly ScriptSnippet[] ScriptSnippets =
    {
        new("repeat", "REPEAT … END", "Repeat a block a fixed number of times.",
            "REPEAT «count»\n    «command»\nEND\n"),
        new("delay", "DELAY", "Pause, in milliseconds. Instruments need time to settle.",
            "DELAY «milliseconds»\n"),
        new("print", "PRINT", "Write a message into the output pane.",
            "PRINT «message»\n"),
        new("idn", "*IDN?", "Ask the instrument what it is. The safe first command.",
            "*IDN?\n"),
        new("comment", "# comment", "A note to whoever reads this next.",
            "# «why this is here»\n"),
        new("settle", "Set, settle, read", "Change something, wait for it, then measure it.",
            "«command to set the value»\n# Let it settle before measuring\nDELAY 500\n«query?»\n"),
    };

    private static readonly ScriptSnippet[] SequenceSnippets =
    {
        new("device", "DEVICE", "Name an instrument. Must come before the alias is used.",
            "DEVICE «alias» : «MODEL»\n"),
        new("with", "WITH … END", "Send a whole block to one instrument.",
            "WITH «alias»\n    «command»\nEND\n"),
        new("for", "FOR … STEP … END", "Sweep a value in equal steps.",
            "FOR «v» = «start» TO «stop» STEP «step»\n    «alias»: «command» $«v»\nEND\n"),
        new("forlog", "FOR … POINTS … LOG … END",
            "Sweep log-spaced — how a filter or a response curve is actually measured.",
            "FOR «f» = 100 TO 100k POINTS 40 LOG\n    «alias»: «command» $«f»\n    DELAY 300\nEND\n"),
        new("capture", "Capture a reply", "Read a value and keep it for later as $name.",
            "«alias»: «query?» -> «name»\n"),
        new("record", "RECORD", "Append a row to the results table.",
            "RECORD $«a», $«b»\n"),
        new("columns", "COLUMNS", "Name the result columns. Put it near the top.",
            "COLUMNS «First», «Second»\n"),
        new("sweep", "Whole swept measurement",
            "The shape most sequences take: declare, name the columns, sweep, record.",
            "DEVICE «gen» : «MODEL»\nDEVICE «meter» : «MODEL»\nCOLUMNS Frequency (Hz), «Reading»\n\n"
          + "FOR f = 100 TO 100k POINTS 40 LOG\n    «gen»: «set frequency» $f\n"
          + "    # Let the circuit and the meter settle\n    DELAY 300\n"
          + "    «meter»: «query?» -> v\n    RECORD $f, $v\nEND\n"),
        new("repeat", "REPEAT … END", "Repeat a block a fixed number of times.",
            "REPEAT «count»\n    «alias»: «command»\nEND\n"),
        new("delay", "DELAY", "Pause, in milliseconds. Instruments need time to settle.",
            "DELAY «milliseconds»\n"),
        new("print", "PRINT", "Write a message into the output pane.",
            "PRINT «message»\n"),
        new("comment", "# comment", "A note to whoever reads this next.",
            "# «why this is here»\n"),
    };

    public static ScriptLanguage ForScript { get; } =
        new(false, ScriptKeywords, Array.Empty<string>(), ScriptSnippets);

    public static ScriptLanguage ForSequence { get; } =
        new(true, SequenceKeywords, SequenceInner, SequenceSnippets);

    public static ScriptLanguage For(bool isSequence) => isSequence ? ForSequence : ForScript;

    // ----------------------------------------------------------------------- tokenizing

    private static readonly Regex Variable = new(@"\$\w+", RegexOptions.Compiled);
    private static readonly Regex Number = new(@"\b\d+(\.\d+)?[kKmMgGuUnNpP]?\b", RegexOptions.Compiled);
    private static readonly Regex Word = new(@"[A-Za-z_*][\w*]*", RegexOptions.Compiled);

    /// <summary>
    /// Break one line into coloured runs.
    ///
    /// Line-at-a-time, which is what lets the editor recolour one line per keystroke instead
    /// of the whole script. The one thing it cannot work out alone is whether a leading
    /// <c>word:</c> addresses an instrument, because <c>C1:BSWV</c> and <c>gen:</c> are the
    /// same shape — so <paramref name="aliases"/> carries the names the script has actually
    /// declared, which is exactly how the runner decides. Pass nothing and no prefix is
    /// claimed as an instrument: saying "this line goes to a device" when it does not is the
    /// one colouring mistake that would mislead rather than merely disappoint.
    /// </summary>
    public IReadOnlyList<ScriptToken> Tokenize(string line, IReadOnlyCollection<string>? aliases = null)
    {
        var tokens = new List<ScriptToken>();
        if (line.Length == 0) return tokens;

        int indent = 0;
        while (indent < line.Length && char.IsWhiteSpace(line[indent])) indent++;
        if (indent == line.Length) return tokens;

        string rest = line[indent..];

        if (rest.StartsWith('#') || rest.StartsWith("//"))
        {
            tokens.Add(new ScriptToken(indent, line.Length - indent, ScriptTokenKind.Comment));
            return tokens;
        }

        int pos = indent;

        // "gen: ..." — an addressed line. Only in the sequence dialect, and only when the
        // name before the colon is one the script declared: ":MEASure:VPP?" and "C1:BSWV"
        // are the same shape and must not be mistaken for one.
        if (IsSequence && aliases is { Count: > 0 })
        {
            int colon = rest.IndexOf(':');
            if (colon > 0 && aliases.Contains(rest[..colon].Trim(), StringComparer.OrdinalIgnoreCase))
            {
                tokens.Add(new ScriptToken(pos, colon, ScriptTokenKind.Alias));
                tokens.Add(new ScriptToken(pos + colon, 1, ScriptTokenKind.Operator));
                pos += colon + 1;
            }
        }

        string body = line[pos..];
        Match head = Word.Match(body);
        bool keywordLine = head.Success && head.Index == 0 && IsKeyword(head.Value);

        if (keywordLine)
        {
            tokens.Add(new ScriptToken(pos, head.Length, ScriptTokenKind.Keyword));

            // DEVICE and WITH name an instrument; colour it as one so the eye can follow an
            // alias from where it is declared to where it is used.
            if (head.Value.Equals("DEVICE", StringComparison.OrdinalIgnoreCase)
                || head.Value.Equals("WITH", StringComparison.OrdinalIgnoreCase))
            {
                Match alias = Word.Match(body, head.Index + head.Length);
                if (alias.Success) tokens.Add(new ScriptToken(pos + alias.Index, alias.Length, ScriptTokenKind.Alias));
            }

            foreach (Match m in Word.Matches(body, head.Index + head.Length))
                if (InnerKeywords.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
                    tokens.Add(new ScriptToken(pos + m.Index, m.Length, ScriptTokenKind.Keyword));
        }
        else if (body.TrimStart().Length > 0)
        {
            Match cmd = Word.Match(body);
            if (cmd.Success) tokens.Add(new ScriptToken(pos, body.TrimEnd().Length, ScriptTokenKind.Command));
        }

        // '-> name' captures a reply. The arrow and the name it binds are worth seeing.
        int arrow = body.IndexOf("->", StringComparison.Ordinal);
        if (arrow >= 0)
        {
            tokens.Add(new ScriptToken(pos + arrow, 2, ScriptTokenKind.Operator));
            Match name = Word.Match(body, arrow + 2);
            if (name.Success) tokens.Add(new ScriptToken(pos + name.Index, name.Length, ScriptTokenKind.Variable));
        }

        foreach (Match m in Number.Matches(body))
            tokens.Add(new ScriptToken(pos + m.Index, m.Length, ScriptTokenKind.Number));

        // Last, so a $name inside anything else wins its colour.
        foreach (Match m in Variable.Matches(body))
            tokens.Add(new ScriptToken(pos + m.Index, m.Length, ScriptTokenKind.Variable));

        return tokens;
    }

    // ---------------------------------------------------------------------- completion

    /// <summary>
    /// What could come next, best first.
    /// </summary>
    /// <param name="script">The whole script, so aliases and captures already written can be offered.</param>
    /// <param name="prefix">The partial word under the caret. Empty offers everything.</param>
    /// <param name="commands">Catalog commands for the instruments in play.</param>
    public IReadOnlyList<ScriptCompletion> Complete(
        string script, string prefix, IEnumerable<string>? commands = null)
    {
        var results = new List<ScriptCompletion>();
        bool wantsVariable = prefix.StartsWith('$');
        string bare = wantsVariable ? prefix[1..] : prefix;

        bool Matches(string candidate)
            => bare.Length == 0
            || candidate.StartsWith(bare, StringComparison.OrdinalIgnoreCase);

        // A '$' has exactly one meaning, so offer nothing else once it is typed.
        if (wantsVariable)
        {
            foreach (string v in CapturedNames(script).Where(Matches))
                results.Add(new ScriptCompletion("$" + v, "captured value", ScriptCompletionKind.Variable));
            return results;
        }

        foreach (ScriptSnippet s in Snippets.Where(s => Matches(s.Trigger)))
            results.Add(new ScriptCompletion(s.Trigger, s.Title + " — " + s.Summary,
                                             ScriptCompletionKind.Snippet, s));

        foreach (string k in Keywords.Concat(InnerKeywords).Distinct().Where(Matches))
            results.Add(new ScriptCompletion(k, "keyword", ScriptCompletionKind.Keyword));

        if (IsSequence)
        {
            foreach (string a in DeclaredAliases(script).Where(Matches))
                results.Add(new ScriptCompletion(a + ":", "instrument", ScriptCompletionKind.Alias));
        }

        if (commands != null)
        {
            foreach (string c in commands.Where(Matches).Take(300))
                results.Add(new ScriptCompletion(c, "command", ScriptCompletionKind.Command));
        }

        return results;
    }

    /// <summary>Aliases the script has declared with DEVICE, in the order written.</summary>
    public static IReadOnlyList<string> DeclaredAliases(string script)
        => SequenceRunner.Requirements(script).Select(r => r.Alias).Distinct().ToList();

    /// <summary>Names bound by <c>-&gt; name</c>, plus FOR loop variables.</summary>
    public static IReadOnlyList<string> CapturedNames(string script)
    {
        var names = new List<string>();

        foreach (Match m in Regex.Matches(script, @"->\s*(\w+)"))
            names.Add(m.Groups[1].Value);

        foreach (Match m in Regex.Matches(script, @"(?im)^\s*FOR\s+(\w+)\s*="))
            names.Add(m.Groups[1].Value);

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public enum ScriptCompletionKind { Snippet, Keyword, Alias, Variable, Command }

/// <param name="Text">What gets inserted when it is chosen — unless it carries a snippet.</param>
/// <param name="Detail">The grey text beside it, saying what kind of thing this is.</param>
public sealed record ScriptCompletion(
    string Text, string Detail, ScriptCompletionKind Kind, ScriptSnippet? Snippet = null);
