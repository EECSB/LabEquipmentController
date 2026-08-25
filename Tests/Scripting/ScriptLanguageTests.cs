using System;
using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The editor's idea of the language, checked against the runners' idea of it.
///
/// These two must not drift. The colouring and the completion list are how someone learns a
/// language nobody has seen before, and a keyword the editor offers but the runner rejects
/// teaches the wrong thing — confidently.
/// </summary>
public class ScriptLanguageTests
{
    private static ScriptLanguage Seq => ScriptLanguage.ForSequence;
    private static ScriptLanguage One => ScriptLanguage.ForScript;

    /// <summary>The aliases a real editor would have found in the script above the line.</summary>
    private static readonly string[] Declared = { "gen", "dmm", "scope" };

    private static ScriptTokenKind KindAt(ScriptLanguage lang, string line, int index)
        => lang.Tokenize(line, Declared)
               .Where(t => index >= t.Start && index < t.Start + t.Length)
               .Select(t => t.Kind)
               .DefaultIfEmpty(ScriptTokenKind.Plain)
               .Last();   // later tokens win, as they do when painted

    private static string Text(string line, ScriptToken t) => line.Substring(t.Start, t.Length);

    // -------------------------------------------------------------------- colouring

    [Theory]
    [InlineData("# a note")]
    [InlineData("    # indented")]
    [InlineData("// also a comment")]
    public void A_comment_is_one_token_covering_the_whole_line(string line)
    {
        ScriptToken t = Assert.Single(Seq.Tokenize(line, Declared));
        Assert.Equal(ScriptTokenKind.Comment, t.Kind);
        Assert.Equal(line.TrimStart(), Text(line, t));
    }

    [Fact]
    public void An_empty_or_blank_line_has_nothing_to_colour()
    {
        Assert.Empty(Seq.Tokenize(""));
        Assert.Empty(Seq.Tokenize("     "));
    }

    [Theory]
    [InlineData("DEVICE gen : SDG2042X")]
    [InlineData("WITH gen")]
    [InlineData("RECORD $f, $v")]
    [InlineData("COLUMNS Frequency, Vout")]
    [InlineData("DELAY 300")]
    [InlineData("END")]
    public void A_line_that_starts_with_a_keyword_has_it_coloured_as_one(string line)
        => Assert.Equal(ScriptTokenKind.Keyword, KindAt(Seq, line, 0));

    [Fact]
    public void An_alias_prefix_is_told_apart_from_a_scpi_header()
    {
        // "gen:" addresses an instrument.
        Assert.Equal(ScriptTokenKind.Alias, KindAt(Seq, "gen: C1:OUTP ON", 0));

        // ":MEASure:VPP?" and "C1:BSWV" are commands, and colouring their first mnemonic as
        // an instrument name would be a lie the eye believes.
        Assert.NotEqual(ScriptTokenKind.Alias, KindAt(Seq, ":MEASure:VPP? CHANnel1", 1));
        Assert.NotEqual(ScriptTokenKind.Alias, KindAt(Seq, "C1:BSWV FRQ,1000", 0));
    }

    /// <summary>A keyword is never an alias, however it is punctuated.</summary>
    [Fact]
    public void A_keyword_before_a_colon_is_still_a_keyword()
        => Assert.Equal(ScriptTokenKind.Keyword, KindAt(Seq, "DEVICE gen : SDG2042X", 0));

    [Fact]
    public void The_instrument_named_by_device_and_with_is_coloured_as_an_alias()
    {
        Assert.Equal(ScriptTokenKind.Alias, KindAt(Seq, "DEVICE gen : SDG2042X", 7));
        Assert.Equal(ScriptTokenKind.Alias, KindAt(Seq, "WITH scope", 5));
    }

    [Fact]
    public void The_words_inside_a_for_line_are_keywords_too()
    {
        const string line = "FOR f = 100 TO 100k POINTS 40 LOG";
        Assert.Equal(ScriptTokenKind.Keyword, KindAt(Seq, line, 0));                    // FOR
        Assert.Equal(ScriptTokenKind.Keyword, KindAt(Seq, line, line.IndexOf("TO")));
        Assert.Equal(ScriptTokenKind.Keyword, KindAt(Seq, line, line.IndexOf("POINTS")));
        Assert.Equal(ScriptTokenKind.Keyword, KindAt(Seq, line, line.IndexOf("LOG")));
    }

    [Fact]
    public void A_substitution_and_a_capture_are_both_marked_as_values()
    {
        const string line = "scope: :MEASure:VRMS? CHANnel1 -> vout";
        Assert.Equal(ScriptTokenKind.Variable, KindAt(Seq, line, line.IndexOf("vout")));

        const string use = "RECORD $f, $vout";
        Assert.Equal(ScriptTokenKind.Variable, KindAt(Seq, use, use.IndexOf("$f")));
        Assert.Equal(ScriptTokenKind.Variable, KindAt(Seq, use, use.IndexOf("$vout")));
    }

    /// <summary>
    /// The single-instrument dialect has no aliases, so a leading "C1:" there is a command
    /// like any other — this is the difference between the two editors.
    /// </summary>
    [Fact]
    public void The_single_instrument_dialect_never_reads_a_prefix_as_an_alias()
        => Assert.DoesNotContain(One.Tokenize("gen: C1:OUTP ON", Declared),
                                 t => t.Kind == ScriptTokenKind.Alias);

    [Fact]
    public void No_token_ever_runs_past_the_end_of_its_line()
    {
        string[] lines =
        {
            "DEVICE gen : SDG2042X", "gen: C1:BSWV FRQ,1k", "FOR f = 1 TO 10 STEP 1",
            "  scope: :MEASure:VPP? CHANnel1 -> v", "RECORD $f, $v", "END", "# note", "*IDN?",
            "->", "$", ":", "gen:", "WITH", "FOR",
        };

        foreach (string line in lines)
            foreach (ScriptToken t in Seq.Tokenize(line, Declared))
            {
                Assert.InRange(t.Start, 0, line.Length);
                Assert.InRange(t.Start + t.Length, 0, line.Length);
            }
    }

    // ------------------------------------------------------- the editor and the runner

    /// <summary>
    /// Every word the editor colours and offers has to be one the runner actually acts on.
    /// The runner is the authority; this list is a copy, and a copy can rot.
    /// </summary>
    [Theory]
    [InlineData("DEVICE gen : SDG2042X\r\nDEVICE dmm : SDM3065X")]
    public void Declared_aliases_come_from_the_same_place_the_runner_reads_them(string script)
        => Assert.Equal(SequenceRunner.Requirements(script).Select(r => r.Alias),
                        ScriptLanguage.DeclaredAliases(script));

    [Fact]
    public void Every_bundled_example_colours_without_a_single_plain_keyword_line()
    {
        // A weak-looking assertion that has teeth: if the keyword list ever drifts from the
        // runner's, the shipped examples stop lighting up and this catches it.
        foreach (SequenceExample ex in SequenceExamples.All)
            foreach (string line in ex.Script.Split("\r\n"))
            {
                string t = line.TrimStart();
                if (t.Length == 0 || t.StartsWith('#')) continue;

                string head = new(t.TakeWhile(char.IsLetter).ToArray());
                if (!Seq.IsKeyword(head)) continue;

                Assert.Contains(Seq.Tokenize(line, Declared), tok => tok.Kind == ScriptTokenKind.Keyword);
            }
    }

    // -------------------------------------------------------------------- completion

    [Fact]
    public void Completion_offers_keywords_snippets_and_catalog_commands()
    {
        IReadOnlyList<ScriptCompletion> hits = Seq.Complete("", "RE", new[] { "RECall", "RST" });

        Assert.Contains(hits, c => c.Kind == ScriptCompletionKind.Keyword && c.Text == "RECORD");
        Assert.Contains(hits, c => c.Kind == ScriptCompletionKind.Snippet && c.Text == "repeat");
        Assert.Contains(hits, c => c.Kind == ScriptCompletionKind.Command && c.Text == "RECall");
    }

    [Fact]
    public void Completion_offers_the_instruments_this_script_has_declared()
    {
        const string script = "DEVICE gen : SDG2042X\r\nDEVICE dmm : SDM3065X\r\n";
        IReadOnlyList<ScriptCompletion> hits = Seq.Complete(script, "d");

        Assert.Contains(hits, c => c.Kind == ScriptCompletionKind.Alias && c.Text == "dmm:");
        Assert.DoesNotContain(hits, c => c.Kind == ScriptCompletionKind.Alias && c.Text == "gen:");
    }

    /// <summary>A '$' means one thing, so nothing else is offered after it.</summary>
    [Fact]
    public void After_a_dollar_only_captured_values_are_offered()
    {
        const string script = "FOR f = 1 TO 10 STEP 1\r\n  dmm: MEAS:VOLT? -> vout\r\nEND";
        IReadOnlyList<ScriptCompletion> hits = Seq.Complete(script, "$");

        Assert.All(hits, c => Assert.Equal(ScriptCompletionKind.Variable, c.Kind));
        Assert.Contains(hits, c => c.Text == "$vout");
        Assert.Contains(hits, c => c.Text == "$f");     // the loop variable counts
    }

    [Fact]
    public void The_single_instrument_dialect_offers_no_aliases_because_it_has_none()
        => Assert.DoesNotContain(One.Complete("DEVICE gen : SDG2042X", ""),
                                 c => c.Kind == ScriptCompletionKind.Alias);

    // ---------------------------------------------------------------------- snippets

    [Fact]
    public void Every_snippet_has_a_trigger_a_description_and_a_body()
        => Assert.All(ScriptLanguage.ForSequence.Snippets.Concat(ScriptLanguage.ForScript.Snippets),
            s =>
            {
                Assert.False(string.IsNullOrWhiteSpace(s.Trigger));
                Assert.False(string.IsNullOrWhiteSpace(s.Title));
                Assert.False(string.IsNullOrWhiteSpace(s.Summary));
                Assert.False(string.IsNullOrWhiteSpace(s.Body));
                Assert.Equal(s.Trigger, s.Trigger.ToLowerInvariant());
            });

    [Fact]
    public void Snippet_triggers_are_unique_within_a_dialect()
    {
        foreach (ScriptLanguage lang in new[] { Seq, One })
            Assert.Equal(lang.Snippets.Count,
                         lang.Snippets.Select(s => s.Trigger).Distinct().Count());
    }

    /// <summary>Every «placeholder» has to close, or Tab walks off the end of the script.</summary>
    [Fact]
    public void Every_placeholder_is_balanced()
        => Assert.All(Seq.Snippets.Concat(One.Snippets), s =>
        {
            Assert.Equal(s.Body.Count(c => c == ScriptSnippet.PlaceholderOpen),
                         s.Body.Count(c => c == ScriptSnippet.PlaceholderClose));
            Assert.Equal(s.Body.Count(c => c == ScriptSnippet.PlaceholderOpen),
                         ScriptSnippet.PlaceholdersIn(s.Body).Count);
        });

    [Fact]
    public void Placeholders_are_found_in_the_order_tab_should_visit_them()
    {
        IReadOnlyList<(int Start, int Length)> found =
            ScriptSnippet.PlaceholdersIn("FOR «v» = «a» TO «b»");

        Assert.Equal(3, found.Count);
        Assert.True(found[0].Start < found[1].Start && found[1].Start < found[2].Start);
    }

    /// <summary>
    /// A snippet body has to be something the runner accepts once its placeholders are
    /// filled — otherwise the editor is teaching a language the app does not run.
    /// </summary>
    [Fact]
    public void Every_sequence_snippet_parses_once_its_placeholders_are_filled()
    {
        foreach (ScriptSnippet s in Seq.Snippets)
        {
            string filled = Fill(s.Body);
            // Requirements() is the runner's own parser for DEVICE lines; it must not throw
            // on anything the Snippets menu can insert.
            Exception? boom = Record.Exception(() => SequenceRunner.Requirements(filled));
            Assert.True(boom == null, $"{s.Trigger}: {boom?.Message}");
        }
    }

    private static string Fill(string body)
        => System.Text.RegularExpressions.Regex.Replace(body, "«[^»]*»", "x");
}
