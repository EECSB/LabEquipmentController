using System;
using System.Collections.Generic;
using System.Linq;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The AI script writer, at the two places it can be wrong without anyone noticing.
///
/// The prompt is one: a model given no catalog writes SCPI from memory, and SCPI from
/// memory is how ":SOURce1:FREQuency 1000" gets sent to a Siglent generator that has never
/// heard of it. So the payload has to actually carry the commands, and it has to carry the
/// ones the request is about, not the first 240 alphabetically.
///
/// The reply is the other. Everything a model returns is a draft, and the check that
/// separates a draft from a fabrication is whether each line's header exists in a catalog.
/// That check is a header check and no more — see
/// <see cref="A_wrong_argument_under_a_documented_header_is_not_caught"/>, which pins where
/// it stops — but it is the same check <see cref="CatalogCoverageTests"/> holds the shipped
/// examples to (SPEC §10).
/// </summary>
public class ScriptAuthorTests
{
    private static CommandReference? Siglent()
        => CommandReference.ForIdentity("Siglent Technologies,SDG2042X,SDG2XCA0000,2.01");

    private static CommandReference? Rigol()
        => CommandReference.ForIdentity("RIGOL TECHNOLOGIES,DS2202A,DS2A000000,00.03");

    private static ScriptContextInstrument Gen(string alias = "")
        => new(alias, "SDG2042X", "Siglent Technologies,SDG2042X,SDG2XCA0000,2.01", Siglent());

    private static ScriptContextInstrument Scope(string alias = "scope")
        => new(alias, "DS2202A", "RIGOL TECHNOLOGIES,DS2202A,DS2A000000,00.03", Rigol());

    private static string Reply(string script, string notes = "")
        => System.Text.Json.JsonSerializer.Serialize(new { script, notes });

    // --------------------------------------------------------------------------- payload

    /// <summary>
    /// The whole point of the feature. Without the catalog in the payload this is just a
    /// chatbot writing SCPI from memory, which is the thing SPEC §10 exists to prevent.
    /// </summary>
    [Fact]
    public void The_payload_carries_the_commands_the_instrument_actually_accepts()
    {
        string payload = ScriptAuthor.BuildPayload(
            "set channel 1 to a 1 kHz sine", new[] { Gen() }, null, null);

        Assert.Contains("SDG2042X", payload);
        Assert.Contains("BSWV", payload);          // the generator's waveform command
        Assert.Contains("set channel 1 to a 1 kHz sine", payload);
    }

    [Fact]
    public void An_instrument_with_no_catalog_says_so_rather_than_saying_nothing()
    {
        string payload = ScriptAuthor.BuildPayload(
            "read the voltage",
            new[] { new ScriptContextInstrument("", "MYSTERY-1", "ACME,MYSTERY-1,0,1", null) },
            null, null);

        Assert.Contains("No command catalog", payload);
        Assert.Contains("*IDN?", payload);
    }

    [Fact]
    public void The_current_script_goes_across_only_when_there_is_one()
    {
        Assert.Contains("currently in the editor",
            ScriptAuthor.BuildPayload("tidy this up", new[] { Gen() }, "*IDN?", null));

        Assert.DoesNotContain("currently in the editor",
            ScriptAuthor.BuildPayload("tidy this up", new[] { Gen() }, "   ", null));
    }

    /// <summary>
    /// "It failed, fix it" is only answerable by something that can see the failure. The run
    /// log is the only place that failure exists, so it has to reach the model.
    /// </summary>
    [Fact]
    public void The_run_output_goes_across_so_an_error_can_be_fixed()
    {
        string payload = ScriptAuthor.BuildPayload(
            "it failed, fix it", new[] { Gen() }, "C1:BSWV FREQ,1000",
            "> C1:BSWV FREQ,1000\r\n-113, \"Undefined header\"");

        Assert.Contains("Undefined header", payload);
        Assert.Contains("-113", payload);
    }

    /// <summary>Trimmed from the front, because the error is at the end of a run log.</summary>
    [Fact]
    public void A_long_run_log_is_trimmed_from_the_front_so_the_last_error_survives()
    {
        string log = "FIRST LINE OF A VERY LONG RUN\r\n" + new string('x', 20000)
                   + "\r\n-113, \"Undefined header\"";
        string payload = ScriptAuthor.BuildPayload("fix it", new[] { Gen() }, null, log);

        Assert.Contains("Undefined header", payload);
        Assert.DoesNotContain("FIRST LINE OF A VERY LONG RUN", payload);
    }

    [Fact]
    public void Every_instrument_is_listed_with_the_alias_the_script_will_use()
    {
        string payload = ScriptAuthor.BuildPayload(
            "sweep and measure", new[] { Gen("gen"), Scope("scope") }, null, null);

        Assert.Contains("gen — SDG2042X", payload);
        Assert.Contains("scope — DS2202A", payload);
    }

    // -------------------------------------------------------------------------- relevance

    /// <summary>
    /// A single R&amp;S analyzer catalog is 2,270 commands. Sending all of them is both
    /// wasteful and worse than sending fewer: the request's own words have to decide which
    /// ones survive, or the model is handed 240 commands beginning with ABORt.
    /// </summary>
    [Fact]
    public void The_commands_sent_are_the_ones_the_request_is_about()
    {
        CommandReference? analyzer = CommandReference.ForFamily(InstrumentFamily.RohdeFslAnalyzer);
        Assert.NotNull(analyzer);
        Assert.True(analyzer!.Commands.Count > 300, "this test needs a catalog worth trimming");

        IReadOnlyList<CommandRef> chosen = ScriptAuthor.Relevant(
            analyzer.Commands, "set the centre frequency and read the marker", 60);

        Assert.Equal(60, chosen.Count);
        Assert.Contains(chosen, c => c.Syntax.Contains("FREQ", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(chosen, c => c.Syntax.Contains("MARK", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_catalog_that_already_fits_is_sent_whole()
    {
        var commands = new[]
        {
            new CommandRef("Common", "*IDN?", "Identify."),
            new CommandRef("Common", "*RST", "Reset."),
        };

        Assert.Equal(2, ScriptAuthor.Relevant(commands, "anything at all", 240).Count);
    }

    // ----------------------------------------------------------------------------- replies

    [Fact]
    public void A_reply_wrapped_in_prose_or_fences_is_still_read()
    {
        string reply = "Here you go!\n```json\n" + Reply("*IDN?", "Reads the identity.") + "\n```";
        AuthoredScript written = ScriptAuthor.Parse(reply, new[] { Gen() });

        Assert.Equal("*IDN?", written.Script);
        Assert.Equal("Reads the identity.", written.Notes);
    }

    [Fact]
    public void A_reply_that_is_not_json_is_refused_rather_than_guessed_at()
        => Assert.Throws<AiException>(
            () => ScriptAuthor.Parse("I could not do that.", new[] { Gen() }));

    [Fact]
    public void An_empty_script_is_refused()
        => Assert.Throws<AiException>(() => ScriptAuthor.Parse(Reply("   "), new[] { Gen() }));

    /// <summary>
    /// A TextBox renders a bare '\n' as nothing, so a script arriving that way lands in the
    /// editor as one very long line. The examples had exactly this bug.
    /// </summary>
    [Fact]
    public void The_script_arrives_with_line_endings_a_text_box_understands()
    {
        AuthoredScript written = ScriptAuthor.Parse(Reply("*IDN?\n*RST\n"), new[] { Gen() });

        Assert.Contains("\r\n", written.Script);
        Assert.DoesNotContain("\n", written.Script.Replace("\r\n", ""));
    }

    // ------------------------------------------------------------------------- the check

    [Fact]
    public void A_documented_command_raises_nothing()
    {
        AuthoredScript written = ScriptAuthor.Parse(
            Reply("# a comment\r\nPRINT setting up\r\nC1:BSWV WVTP,SINE,FRQ,1000\r\nDELAY 500"),
            new[] { Gen() });

        Assert.Empty(written.Undocumented);
    }

    /// <summary>
    /// The one this feature exists to catch. Setting a generator's frequency is
    /// ":SOURce1:FREQuency 1000" on most of the bench and "C1:BSWV FRQ,1000" on a Siglent;
    /// a model reaching for the familiar spelling produces a command the instrument answers
    /// with an error, or on a bad day ignores.
    /// </summary>
    [Fact]
    public void A_command_written_from_another_vendors_dialect_is_flagged()
    {
        AuthoredScript written = ScriptAuthor.Parse(
            Reply(":SOURce1:FREQuency 1000"), new[] { Gen() });

        Assert.Single(written.Undocumented);
        Assert.Contains("FREQuency", written.Undocumented[0]);
    }

    /// <summary>
    /// Where the check stops, pinned so nobody reads more into it than it does.
    ///
    /// <see cref="ScpiSyntax.Matches"/> compares headers, so a wrong argument under a
    /// documented header passes: "C1:BSWV FREQ,1000" is Siglent's own "C<n>:BSWV" carrying
    /// the wrong mnemonic — the guide says FRQ — and header matching cannot see that. It is
    /// caught on the bench, by the instrument, not here. The warning under a generated
    /// script therefore means "this header is undocumented", not "the rest is verified".
    /// </summary>
    [Fact]
    public void A_wrong_argument_under_a_documented_header_is_not_caught()
    {
        AuthoredScript written = ScriptAuthor.Parse(
            Reply("C1:BSWV FREQ,1000"), new[] { Gen() });

        Assert.Empty(written.Undocumented);
    }

    [Fact]
    public void Keywords_and_comments_are_not_mistaken_for_commands()
    {
        AuthoredScript written = ScriptAuthor.Parse(
            Reply("# comment\r\n// also a comment\r\nPRINT hello\r\nDELAY 100\r\n"
                + "REPEAT 3\r\n*IDN?\r\nEND"),
            new[] { Gen() });

        Assert.Empty(written.Undocumented);
    }

    /// <summary>Each line is checked against the catalog of the instrument it is sent to —
    /// which is the whole reason a sequence's DEVICE line names a model.</summary>
    [Fact]
    public void In_a_sequence_each_line_is_checked_against_its_own_instrument()
    {
        var bench = new[] { Gen("gen"), Scope("scope") };

        AuthoredScript ok = ScriptAuthor.Parse(
            Reply("DEVICE gen : SDG2042X\r\nDEVICE scope : DS2202A\r\n"
                + "gen: C1:OUTP ON\r\nscope: :MEASure:VPP? CHANnel1"),
            bench);
        Assert.Empty(ok.Undocumented);

        // The same two commands, sent to each other's instrument.
        AuthoredScript swapped = ScriptAuthor.Parse(
            Reply("DEVICE gen : SDG2042X\r\nDEVICE scope : DS2202A\r\n"
                + "scope: C1:OUTP ON\r\ngen: :MEASure:VPP? CHANnel1"),
            bench);
        Assert.Equal(2, swapped.Undocumented.Count);
    }

    [Fact]
    public void A_with_block_addresses_its_instrument_for_every_line_inside_it()
    {
        AuthoredScript written = ScriptAuthor.Parse(
            Reply("DEVICE gen : SDG2042X\r\nDEVICE scope : DS2202A\r\n"
                + "WITH gen\r\nC1:BSWV WVTP,SINE\r\nC1:OUTP ON\r\nEND\r\n"
                + "scope: :MEASure:VPP? CHANnel1"),
            new[] { Gen("gen"), Scope("scope") });

        Assert.Empty(written.Undocumented);
    }

    [Fact]
    public void A_captured_reply_is_checked_without_its_capture_name()
    {
        AuthoredScript written = ScriptAuthor.Parse(
            Reply("DEVICE scope : DS2202A\r\nscope: :MEASure:VPP? CHANnel1 -> vpp"),
            new[] { Scope("scope") });

        Assert.Empty(written.Undocumented);
    }

    /// <summary>
    /// No catalog means no opinion. Someone driving an instrument whose guide was never
    /// transcribed still gets a script; flagging every line of it would say nothing.
    /// </summary>
    [Fact]
    public void Nothing_is_flagged_when_there_is_no_catalog_to_flag_it_against()
    {
        AuthoredScript written = ScriptAuthor.Parse(
            Reply("SOME:MADE:UP:COMMAND 5"),
            new[] { new ScriptContextInstrument("", "MYSTERY-1", "ACME,MYSTERY-1,0,1", null) });

        Assert.Empty(written.Undocumented);
    }
}
