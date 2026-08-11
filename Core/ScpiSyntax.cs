using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LabEquipmentController;

/// <summary>
/// Decides whether a concrete SCPI command is an instance of a documented syntax
/// template — whether ":OUTPut1 ON" is covered by ":OUTPut[&lt;n&gt;][:STATe] {ON|1|OFF|0}".
///
/// Programming guides print templates, not sendable strings, using three conventions
/// this has to understand:
///
/// <list type="bullet">
/// <item>Mixed case marks the abbreviation: <c>FUNCtion</c> may be sent as
///       <c>FUNC</c> or <c>FUNCtion</c>, and nothing in between.</item>
/// <item>Square brackets mark an optional node: <c>:OUTPut[:STATe]</c> may be written
///       <c>:OUTPut</c> or <c>:OUTPut:STATe</c>. Vendors bracket the leading root too
///       (<c>[:SOURce[&lt;n&gt;]]:VOLTage</c>, <c>[SOURce:]VOLTage</c>).</item>
/// <item>A <c>&lt;n&gt;</c> suffix marks a channel number: <c>:OUTPut[&lt;n&gt;]</c>
///       matches <c>:OUTPut</c> and <c>:OUTPut2</c> alike.</item>
/// </list>
///
/// This exists so the shipped quick-command buttons and script examples can be checked
/// against the catalogs automatically rather than by eye — SPEC §10 says never invent
/// SCPI, and a test is the only thing that keeps that true as the catalogs grow.
/// </summary>
public static class ScpiSyntax
{
    /// <summary>The header of a command: everything before the first space, without a trailing "?".</summary>
    public static string HeaderOf(string? command)
    {
        string s = (command ?? "").Trim();
        int sp = s.IndexOf(' ');
        if (sp > 0) s = s[..sp];
        return s.TrimEnd('?');
    }

    /// <summary>
    /// True when <paramref name="template"/> is a syntactically plausible SCPI command —
    /// a colon-separated header of mnemonics, optionally with parameters after a space.
    ///
    /// This is a shape check, not a truth check: it cannot tell a real command from a
    /// well-formed invention. Its job is narrower, and it is the gate on AI-extracted
    /// output (<see cref="CommandExtractor"/>) — prose, table furniture and page numbers
    /// all fail it, which is most of what a model wrongly offers up.
    /// </summary>
    public static bool IsValidTemplate(string? template)
    {
        string header = HeaderOf(template);
        if (header.Length == 0 || header.Length > 200) return false;

        // A leading '*' marks an IEEE 488.2 common command: *IDN, *RST, *SAV.
        if (header[0] == '*')
            return header.Length >= 3 && header[1..].All(c => char.IsLetter(c) || char.IsDigit(c));

        // Otherwise every character must belong to a mnemonic, a suffix, or the punctuation
        // vendors use for optional nodes and alternatives.
        foreach (char c in header)
        {
            // '-' spans a suffix range, as Tektronix writes "HAR<1-400>" and Keysight
            // "PIN<1-3>". '?' can land mid-header when a guide prints a query with its
            // optional argument attached — "HISTogram:POINts?[{MIN|MAX|DEF}]" — and rejecting
            // that costs a real command over a missing space.
            // ',' separates arguments inside a group a guide attached without a space, as in
            // "MEASure:TEMPerature?[{RTD|THER}[,{<type>|DEFault}]]". It cannot let prose in:
            // HeaderOf cuts at the first space, so a comma'd sentence still arrives as one
            // word and still has to look like a mnemonic.
            // '.' spans a suffix range the way R&S writes them — "DELTamarker<1...4>",
            // "WINDow<1...4>" — which is most of their command reference. It cannot let prose
            // in either: a sentence still arrives cut at its first space and still has to look
            // like a mnemonic. The cost is that a stray trailing full stop no longer fails
            // here, and the review step is where that gets caught.
            // '…' is the same range, typeset. The R&S manual uses both "<1...4>" and
            // "<1…4>", sometimes on adjacent lines, and a PDF extractor faithfully reproduces
            // whichever it finds.
            bool ok = char.IsLetterOrDigit(c) || c is ':' or '_' or '[' or ']' or '<' or '>'
                                              or '{' or '}' or '|' or '(' or ')' or '@' or '#'
                                              or '-' or '?' or ',' or '.' or '…';
            if (!ok) return false;
        }

        if (!header.Any(char.IsLetter)) return false;

        // Brackets must balance, or it is a fragment rather than a command.
        if (header.Count(c => c == '[') != header.Count(c => c == ']')) return false;
        if (header.Count(c => c == '<') != header.Count(c => c == '>')) return false;

        // Optional-node brackets are stripped before splitting, so that a bracketed root —
        // "[SOURce:]VOLTage[:LEVel]" — divides into nodes the same way an unbracketed one does.
        string bare = new(header.Where(c => c is not ('[' or ']' or '(' or ')')).ToArray());
        string[] nodes = bare.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (nodes.Length == 0) return false;

        // HeaderOf strips the '?', so ask the original whether this was a query.
        bool isQuery = (template ?? "").TrimEnd().Split(' ')[0].EndsWith('?');

        // One recognisable mnemonic is enough, and every node has to at least be shaped like
        // one. Demanding that *every* node look like a mnemonic is what used to throw away a
        // third of a real extraction: plenty of nodes are not mnemonics at all — a choice
        // between them ({AC|DC}), a placeholder for one (<function>), a bare index (A, Y, X1)
        // — and they sit beside a node that unmistakably is.
        bool anyPlain = false, anyMnemonic = false;

        foreach (string node in nodes)
        {
            if (node.Length == 0) return false;

            // A choice between nodes is as good as its alternatives.
            if (node[0] == '{')
            {
                if (node[^1] != '}') return false;
                string inner = node[1..^1];
                if (inner.Length == 0) return false;
                if (inner.Split('|').Any(a => a.Trim().Length == 0)) return false;
                if (inner.Split('|').Any(a => LooksLikeMnemonic(a.Trim(), isQuery)))
                    anyMnemonic = true;
                continue;
            }

            anyPlain = true;
            string word = StripSuffixNames(node);

            // A node that is nothing but a placeholder — ":MEASure:<function>?" — says which
            // one, not what it is. Legal beside a node that names something.
            if (word.Length == 0) continue;

            if (!char.IsLetter(word[0])) return false;
            if (LooksLikeMnemonic(word, isQuery)) anyMnemonic = true;
        }

        // Something has to name a thing, or this is prose. And a command cannot be only a
        // choice of parameters: "{ON|OFF}" alone is a wrapped PDF line that lost its command,
        // which is a shape these guides produce constantly.
        return anyPlain && anyMnemonic;
    }

    /// <summary>
    /// Does this word name something, the way a SCPI mnemonic does?
    ///
    /// Two capitals, not necessarily adjacent. Every mnemonic carries its short form in
    /// capitals — MEASure, CHANnel, RUN, and Siglent's ParaCoPy, whose capitals are spread
    /// out. Prose does not: a sentence reaching here has been cut at its first space by
    /// HeaderOf, so what arrives is "The", "Set", "returns" or "This", each with one capital
    /// at most.
    ///
    /// A one-letter mnemonic counts only in a query, because `R?` is the only such command in
    /// any guide here and "A" is also how a sentence begins.
    /// </summary>
    private static bool LooksLikeMnemonic(string word, bool isQuery)
    {
        if (word.Length == 0 || !char.IsLetter(word[0])) return false;

        int capitals = word.Count(char.IsUpper);
        if (capitals >= 2) return true;

        return isQuery && capitals == 1 && word.Length == 1;
    }

    /// <summary>Drop "&lt;n&gt;"-style suffixes, leaving the mnemonic they qualify.</summary>
    private static string StripSuffixNames(string node)
    {
        var sb = new System.Text.StringBuilder();
        bool inside = false;
        foreach (char c in node)
        {
            if (c == '<') { inside = true; continue; }
            if (c == '>') { inside = false; continue; }
            if (!inside && !char.IsDigit(c)) sb.Append(c);
        }
        return sb.ToString();
    }

    private static bool HasCapitalRun(string s)
    {
        int run = 0;
        foreach (char c in s)
        {
            if (char.IsUpper(c)) { if (++run >= 2) return true; }
            else run = 0;
        }
        return false;
    }

    /// <summary>True when <paramref name="command"/> is an instance of <paramref name="template"/>.</summary>
    public static bool Matches(string? command, string? template)
    {
        string cmd = HeaderOf(command);
        string tpl = HeaderOf(template);
        if (cmd.Length == 0 || tpl.Length == 0) return false;

        // A query must be covered by a template that documents the query form, and a
        // set by one that documents the set form — ":OUTPut?" is not a use of a
        // set-only entry, and treating it as one would defeat the point of the check.
        bool cmdQuery = (command ?? "").Contains('?');
        bool tplQuery = (template ?? "").Contains('?');
        if (cmdQuery != tplQuery) return false;

        // IEEE 488.2 common commands are matched whole; they have no keyword tree.
        if (cmd.StartsWith('*') || tpl.StartsWith('*'))
            return string.Equals(cmd, tpl, StringComparison.OrdinalIgnoreCase);

        List<string> actual = cmd.Split(':', StringSplitOptions.RemoveEmptyEntries).ToList();
        return actual.Count > 0 && MatchNodes(actual, ParseTemplate(tpl), 0, 0);
    }

    /// <summary>True when the command is an instance of any of the documented templates.</summary>
    public static bool MatchesAny(string? command, IEnumerable<string> templates)
        => templates.Any(t => Matches(command, t));

    /// <summary>
    /// One node of a documented template. <paramref name="Alternatives"/> holds the
    /// mnemonics the node accepts — usually one, but guides write a choice inline as
    /// <c>:{AC|DC}:RANGe</c>, and any of those spellings is a valid instance.
    /// </summary>
    /// <param name="Numbered">
    /// The mnemonic carries a channel suffix — <c>CHANnel&lt;n&gt;</c>, <c>MARKer&lt;n&gt;</c> —
    /// so a trailing digit on the concrete command is expected.
    /// </param>
    /// <param name="Wildcard">
    /// The whole node is a placeholder standing for a sub-path rather than one
    /// mnemonic. Keithley documents a measurement as <c>:MEASure:&lt;function&gt;?</c>,
    /// where <c>&lt;function&gt;</c> is any of <c>VOLTage[:DC]</c>, <c>CURRent:AC</c>,
    /// <c>RESistance</c>… — so it matches one node or several, and
    /// <c>:MEASure:VOLTage:DC?</c> is a legitimate instance of it.
    /// </param>
    private readonly record struct Node(string[] Alternatives, bool Optional, bool Numbered, bool Wildcard);

    /// <summary>
    /// Split a template into nodes, walking it a character at a time so that the
    /// bracket nesting vendors use — "[:SOURce[&lt;n&gt;]]:VOLTage[:LEVel]" — is read
    /// correctly. A node is optional when its first letter sits inside a bracket.
    /// </summary>
    private static List<Node> ParseTemplate(string header)
    {
        var nodes = new List<Node>();
        var cur = new StringBuilder();
        int depth = 0, curDepth = 0;
        bool numbered = false;

        void Flush()
        {
            string raw = cur.ToString();
            bool wasNumbered = numbered;
            bool wasWildcard = numbered && raw.Length == 0;   // the node was only "<...>"
            cur.Clear();
            numbered = false;

            if (wasWildcard)
            {
                nodes.Add(new Node(Array.Empty<string>(), curDepth > 0, false, true));
                return;
            }
            if (raw.Length == 0) return;

            // "{AC|DC}" is a choice of mnemonics for this one node.
            string[] alts = raw.Split('|', StringSplitOptions.RemoveEmptyEntries);
            if (alts.Length > 0) nodes.Add(new Node(alts, curDepth > 0, wasNumbered, false));
        }

        for (int i = 0; i < header.Length; i++)
        {
            char c = header[i];
            switch (c)
            {
                case '[':
                    // "[1]" is an optional channel suffix, the same idea as "<n>" —
                    // Keithley writes ":SOURce[1]:FUNCtion" where others write
                    // ":SOURce<n>:FUNCtion". Without this the digit is absorbed into
                    // the mnemonic and ":SOURce:FUNCtion" stops matching.
                    int close = header.IndexOf(']', i);
                    if (cur.Length > 0 && close > i + 1 &&
                        header.AsSpan(i + 1, close - i - 1).ToString().All(char.IsDigit))
                    {
                        numbered = true;
                        i = close;
                        break;
                    }
                    depth++;
                    break;
                case ']': if (depth > 0) depth--; break;
                case ':': Flush(); break;
                case '<':
                    // A placeholder. On its own it is a whole-node wildcard; after a
                    // mnemonic it is a channel suffix. Flush() tells them apart by
                    // whether any letters were collected before it.
                    if (cur.Length == 0) curDepth = depth;
                    numbered = true;
                    while (i < header.Length && header[i] != '>') i++;
                    break;
                case '|':
                    cur.Append('|');
                    break;
                default:
                    // Underscores are part of a mnemonic, not punctuation: Tektronix
                    // writes "WFMOutpre:NR_Pt?", "WFMOutpre:BYT_NR", "DATa:BN_FMT".
                    if (char.IsLetterOrDigit(c) || c == '_')
                    {
                        if (cur.Length == 0) curDepth = depth;
                        cur.Append(c);
                    }
                    break;
            }
        }
        Flush();
        return nodes;
    }

    /// <summary>Walk template nodes against the command's nodes, skipping optional ones as needed.</summary>
    private static bool MatchNodes(List<string> cmd, List<Node> tpl, int ci, int ti)
    {
        while (true)
        {
            if (ci == cmd.Count && ti == tpl.Count) return true;

            // Command exhausted: a match only if every remaining template node is optional.
            if (ci == cmd.Count) return tpl.Skip(ti).All(n => n.Optional);

            // Template exhausted while the command has nodes left: not a match.
            if (ti == tpl.Count) return false;

            Node n = tpl[ti];

            if (n.Wildcard)
            {
                // A placeholder stands for a sub-path: try it against one node, then
                // two, and so on. Leave enough behind for the remaining required nodes.
                int required = tpl.Skip(ti + 1).Count(x => !x.Optional && !x.Wildcard);
                for (int take = 1; ci + take + required <= cmd.Count; take++)
                    if (MatchNodes(cmd, tpl, ci + take, ti + 1)) return true;
                return n.Optional && MatchNodes(cmd, tpl, ci, ti + 1);
            }

            bool here = MnemonicMatches(cmd[ci], n);

            if (n.Optional)
            {
                // Try consuming the optional node; failing that, skip it and go on.
                if (here && MatchNodes(cmd, tpl, ci + 1, ti + 1)) return true;
                ti++;
                continue;
            }

            if (!here) return false;
            ci++;
            ti++;
        }
    }

    /// <summary>
    /// A concrete mnemonic matches a documented one if it is the full word or the
    /// short form — the leading run of capitals — optionally with a channel digit.
    /// </summary>
    private static bool MnemonicMatches(string actual, Node node)
    {
        string a = actual;
        if (node.Numbered)
        {
            int end = a.Length;
            while (end > 0 && char.IsDigit(a[end - 1])) end--;
            if (end > 0) a = a[..end];      // keep at least one letter
        }

        foreach (string full in node.Alternatives)
        {
            // A mnemonic carrying an underscore ("NR_Pt", "BYT_NR") has no separate
            // abbreviation — the capitals run stops at the underscore and would
            // otherwise leave a misleading two-letter short form like "NR".
            string shortForm = full.Contains('_')
                ? full
                : new string(full.TakeWhile(char.IsUpper).ToArray());
            if (shortForm.Length == 0) shortForm = full;

            if (a.Equals(full, StringComparison.OrdinalIgnoreCase)
             || a.Equals(shortForm, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
