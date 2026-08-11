using System;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using LabEquipmentController;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// Pins the three request shapes and the reply paths. These are transcribed from each
/// provider's REST documentation and are the part most likely to be silently wrong — a
/// misspelled key produces a 400 at the worst possible moment, on a user's own key.
/// </summary>
public class AiRequestTests
{
    private static readonly JsonNode Schema = JsonNode.Parse("""{"type":"object"}""")!;

    private static AiConnection Conn(AiProvider p) => new() { Provider = p };

    private static JsonNode Body(AiProvider p, AiPayload payload)
        => JsonNode.Parse(AiRequest.Body(Conn(p), "Extract the commands.", payload, Schema))!;

    private static AiPayload Pdf() => AiPayload.FromDocument(Encoding.ASCII.GetBytes("%PDF-1.4"), "application/pdf");

    // ------------------------------------------------------------------------------ paths

    [Theory]
    [InlineData(AiProvider.Gemini, "/v1beta/interactions")]
    [InlineData(AiProvider.Anthropic, "/v1/messages")]
    [InlineData(AiProvider.OpenAiCompatible, "/v1/chat/completions")]
    public void Each_provider_posts_to_its_own_path(AiProvider p, string expected)
        => Assert.Equal(expected, AiRequest.PathFor(p));

    // ----------------------------------------------------------------------------- gemini

    [Fact]
    public void Gemini_sends_a_pdf_as_an_inline_document_part()
    {
        JsonNode body = Body(AiProvider.Gemini, Pdf());

        JsonNode doc = body["input"]![0]!;
        Assert.Equal("document", doc["type"]!.GetValue<string>());
        Assert.Equal("application/pdf", doc["mime_type"]!.GetValue<string>());
        Assert.Equal(Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.4")),
                     doc["data"]!.GetValue<string>());

        Assert.Equal("text", body["input"]![1]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Gemini_asks_for_json_against_the_schema()
    {
        JsonNode rf = Body(AiProvider.Gemini, AiPayload.FromText("x"))["response_format"]!;

        Assert.Equal("text", rf["type"]!.GetValue<string>());
        Assert.Equal("application/json", rf["mime_type"]!.GetValue<string>());
        Assert.NotNull(rf["schema"]);
    }

    [Fact]
    public void Gemini_text_only_requests_carry_no_document_part()
    {
        JsonNode body = Body(AiProvider.Gemini, AiPayload.FromText("a datasheet"));

        Assert.Single(body["input"]!.AsArray());
        Assert.Contains("a datasheet", body["input"]![0]!["text"]!.GetValue<string>());
    }

    // -------------------------------------------------------------------------- anthropic

    [Fact]
    public void Anthropic_sends_a_pdf_as_a_base64_document_block()
    {
        JsonNode body = Body(AiProvider.Anthropic, Pdf());

        JsonNode block = body["messages"]![0]!["content"]![0]!;
        Assert.Equal("document", block["type"]!.GetValue<string>());
        Assert.Equal("base64", block["source"]!["type"]!.GetValue<string>());
        Assert.Equal("application/pdf", block["source"]!["media_type"]!.GetValue<string>());
    }

    [Fact]
    public void Anthropic_always_sets_max_tokens_because_the_api_requires_it()
        => Assert.True(Body(AiProvider.Anthropic, AiPayload.FromText("x"))["max_tokens"]!.GetValue<int>() > 0);

    // ----------------------------------------------------------------------------- openai

    [Fact]
    public void OpenAi_asks_for_a_json_object()
        => Assert.Equal("json_object",
            Body(AiProvider.OpenAiCompatible, AiPayload.FromText("x"))["response_format"]!["type"]!.GetValue<string>());

    [Fact]
    public void OpenAi_refuses_a_document_rather_than_sending_something_it_cannot_read()
        => Assert.Throws<NotSupportedException>(
            () => AiRequest.Body(Conn(AiProvider.OpenAiCompatible), "go", Pdf(), Schema));

    // -------------------------------------------------------------------------- responses

    [Fact]
    public void Reads_gemini_output_text()
        => Assert.Equal("{\"a\":1}",
            AiRequest.TextFrom(AiProvider.Gemini, """{"output_text":"{\"a\":1}"}"""));

    /// <summary>
    /// The shape a live gemini-3.6-flash reply actually has: a steps array whose first entry
    /// is an opaque "thought" and whose answer sits in a later step's content. Reading
    /// output_text alone returned the whole envelope and every extraction came back empty.
    /// </summary>
    [Fact]
    public void Reads_gemini_answer_out_of_the_steps_array()
    {
        const string live = """
            {"id":"v1_abc","status":"completed",
             "usage":{"total_tokens":1331},
             "steps":[{"signature":"EtoQCtcQARFNMg8EjM2QXtGiaPbS","type":"thought"},
                      {"content":[{"text":"{\"commands\":[]}"}]}]}
            """;

        Assert.Equal("{\"commands\":[]}", AiRequest.TextFrom(AiProvider.Gemini, live));
    }

    [Fact]
    public void A_gemini_reply_that_is_only_a_thought_is_not_mistaken_for_an_answer()
    {
        const string thoughtOnly = """
            {"status":"completed","steps":[{"signature":"abc","type":"thought"}]}
            """;

        // Nothing usable, so the whole body comes back and the caller reports it rather than
        // silently extracting zero commands.
        Assert.Equal(thoughtOnly.Trim(), AiRequest.TextFrom(AiProvider.Gemini, thoughtOnly).Trim());
    }

    [Fact]
    public void Reads_anthropic_first_text_block()
        => Assert.Equal("hello",
            AiRequest.TextFrom(AiProvider.Anthropic, """{"content":[{"type":"text","text":"hello"}]}"""));

    [Fact]
    public void Reads_openai_message_content()
        => Assert.Equal("hello",
            AiRequest.TextFrom(AiProvider.OpenAiCompatible,
                """{"choices":[{"message":{"content":"hello"}}]}"""));

    [Fact]
    public void An_unrecognised_reply_is_handed_back_whole_rather_than_swallowed()
    {
        const string odd = """{"something":"unexpected"}""";
        Assert.Equal(odd, AiRequest.TextFrom(AiProvider.Gemini, odd));
    }

    [Fact]
    public void Errors_surface_the_providers_own_message()
        => Assert.Equal("You exceeded your quota.",
            AiRequest.ErrorFrom("""{"error":{"message":"You exceeded your quota."}}"""));

    [Fact]
    public void An_unreadable_error_body_is_truncated_not_dumped()
    {
        string huge = new('x', 5000);
        Assert.True(AiRequest.ErrorFrom(huge).Length < 500);
    }
}

/// <summary>
/// The rules behind the "extract text locally before sending" checkbox. Where a provider
/// cannot take an uploaded PDF the choice is not a choice, and that has to hold however the
/// stored preference was left.
/// </summary>
public class AiConnectionTests
{
    [Fact]
    public void A_new_connection_follows_its_providers_preference_rather_than_a_fixed_default()
    {
        var cn = new AiConnection();

        Assert.True(cn.FollowsProviderDefault);
        Assert.Null(cn.ExtractTextLocally);
    }

    [Theory]
    // Off wherever the provider can read the file itself — uploading keeps the page layout,
    // and flattening a two-column guide is what attaches a description to the wrong command.
    // On only where there is no alternative.
    [InlineData(AiProvider.Gemini, false)]
    [InlineData(AiProvider.Anthropic, false)]
    [InlineData(AiProvider.OpenAiCompatible, true)]
    public void The_default_is_off_except_where_the_provider_cannot_take_a_file(
        AiProvider provider, bool expected)
        => Assert.Equal(expected, new AiConnection { Provider = provider }.EffectiveExtractTextLocally);

    [Theory]
    [InlineData(AiProvider.Gemini)]
    [InlineData(AiProvider.Anthropic)]
    [InlineData(AiProvider.OpenAiCompatible)]
    public void The_hover_text_says_what_it_does_why_it_exists_and_what_it_costs(AiProvider provider)
    {
        string help = new AiConnection { Provider = provider }.LocalExtractionHelp;

        Assert.Contains("instead of uploading the file", help);
        Assert.Contains("Cost:", help);
        Assert.Contains(AiProviderInfo.For(provider).PdfCostNote, help);
        // A provider that cannot take a file must say so rather than imply a free choice.
        if (!AiProviderInfo.For(provider).SupportsPdfUpload)
            Assert.Contains("Forced on", help);
    }

    [Fact]
    public void An_explicit_choice_overrides_the_provider_default()
    {
        var cn = new AiConnection { Provider = AiProvider.Gemini, ExtractTextLocally = true };

        Assert.False(cn.FollowsProviderDefault);
        Assert.True(cn.EffectiveExtractTextLocally);
    }

    [Theory]
    // The gate on AI output: a shape check, not a truth check. Prose and page furniture fail.
    [InlineData("MEASure:VOLTage:DC?", true)]
    [InlineData("*IDN?", true)]
    [InlineData("[SOURce:]VOLTage[:LEVel]", true)]
    [InlineData(":CHANnel<n>:COUPling {AC|DC}", true)]
    [InlineData("SENSe:FUNCtion \"VOLT:DC\"", true)]
    [InlineData("The instrument returns the measured voltage.", false)]
    [InlineData("Table 4-1: Command summary", false)]
    [InlineData("", false)]
    [InlineData("123", false)]
    [InlineData("[SOURce:VOLTage", false)]        // unbalanced
    [InlineData(":123:456", false)]               // nodes must start with a letter
    public void IsValidTemplate_gates_on_shape(string candidate, bool expected)
        => Assert.Equal(expected, ScpiSyntax.IsValidTemplate(candidate));

    [Fact]
    public void Providers_that_cannot_take_a_pdf_extract_locally_even_when_switched_off()
    {
        var cn = new AiConnection { Provider = AiProvider.OpenAiCompatible, ExtractTextLocally = false };

        Assert.True(cn.ExtractLocallyFor(DocumentKind.Pdf));
        Assert.False(cn.CanSendPdfDirectly);   // so the checkbox is disabled, not merely ticked
    }

    [Theory]
    [InlineData(AiProvider.Gemini)]
    [InlineData(AiProvider.Anthropic)]
    public void Providers_that_can_take_a_pdf_honour_the_choice(AiProvider provider)
    {
        var cn = new AiConnection { Provider = provider, ExtractTextLocally = false };

        Assert.False(cn.ExtractLocallyFor(DocumentKind.Pdf));
        Assert.True(cn.CanSendPdfDirectly);

        cn.ExtractTextLocally = true;
        Assert.True(cn.ExtractLocallyFor(DocumentKind.Pdf));
    }

    [Theory]
    [InlineData(DocumentKind.Text)]
    [InlineData(DocumentKind.Word)]
    public void Everything_that_is_not_a_pdf_is_always_read_locally(DocumentKind kind)
        => Assert.True(new AiConnection { Provider = AiProvider.Gemini, ExtractTextLocally = false }
                        .ExtractLocallyFor(kind));

    [Fact]
    public void Blank_base_url_and_model_fall_back_to_the_providers_defaults()
    {
        var cn = new AiConnection { Provider = AiProvider.Gemini };

        Assert.Equal(AiProviderInfo.For(AiProvider.Gemini).DefaultBaseUrl, cn.EffectiveBaseUrl);
        Assert.Equal(AiProviderInfo.For(AiProvider.Gemini).DefaultModel, cn.EffectiveModel);
    }

    // ------------------------------------------------------------------- upload limits

    private static AiProviderInfo Info(AiProvider p) => AiProviderInfo.For(p);

    private const long Mb = 1024 * 1024;

    [Fact]
    public void A_normal_guide_is_allowed_through()
        => Assert.Null(AiUploadLimits.Check(Info(AiProvider.Anthropic), "guide.pdf", 4 * Mb, 320));

    [Fact]
    public void Anthropic_counts_the_base64_expansion_not_the_raw_file()
    {
        // 25 MB is under the 32 MB cap raw, but base64 makes it ~33.3 MB on the wire, which
        // is not. Comparing the raw size would let this through and fail at the provider.
        string? refusal = AiUploadLimits.Check(Info(AiProvider.Anthropic), "guide.pdf", 25 * Mb, 100);

        Assert.NotNull(refusal);
        Assert.Contains("once encoded", refusal);
        Assert.Contains("Extract text locally", refusal);
    }

    [Fact]
    public void Gemini_counts_the_file_itself_because_that_is_what_its_limit_is_on()
    {
        // The same 25 MB file is fine for Gemini, whose 50 MB cap is on the file.
        Assert.Null(AiUploadLimits.Check(Info(AiProvider.Gemini), "guide.pdf", 25 * Mb, 100));
        Assert.NotNull(AiUploadLimits.Check(Info(AiProvider.Gemini), "guide.pdf", 60 * Mb, 100));
    }

    [Theory]
    [InlineData(AiProvider.Anthropic, 601)]
    [InlineData(AiProvider.Gemini, 1001)]
    public void Too_many_pages_is_refused_with_the_count(AiProvider provider, int pages)
    {
        string? refusal = AiUploadLimits.Check(Info(provider), "guide.pdf", 2 * Mb, pages);

        Assert.NotNull(refusal);
        Assert.Contains("pages", refusal);
        Assert.Contains(pages.ToString("N0"), refusal);
    }

    [Fact]
    public void An_unknown_page_count_does_not_block_an_otherwise_fine_upload()
        => Assert.Null(AiUploadLimits.Check(Info(AiProvider.Anthropic), "guide.pdf", 2 * Mb, 0));

    [Fact]
    public void A_provider_that_cannot_take_a_file_says_so_rather_than_quoting_a_size()
    {
        string? refusal = AiUploadLimits.Check(
            Info(AiProvider.OpenAiCompatible), "guide.pdf", 1 * Mb, 10);

        Assert.NotNull(refusal);
        Assert.Contains("cannot accept an uploaded document", refusal);
    }

    [Fact]
    public void A_trailing_slash_on_the_base_url_does_not_double_up_in_the_path()
        => Assert.Equal("https://example.test",
            new AiConnection { BaseUrl = "https://example.test/" }.EffectiveBaseUrl);

    /// <summary>
    /// The configured timeout has to be the only one, or it is not a setting.
    ///
    /// HttpClient defaults to 100 seconds. Left at that it fired first on any longer request
    /// and the handler reported the *configured* figure anyway, so asking for 600 produced
    /// "did not answer within 600s" after a hundred — the dialog offers up to 900 and
    /// everything above 100 had silently never worked. The whole reason a long timeout exists
    /// is a full programming guide sent to a provider that takes PDFs whole, which is one
    /// request over a hundred-odd pages.
    ///
    /// Reached by reflection because the client is a shared static, and the alternative is
    /// widening the surface of a class whose only public method makes a network call. A test
    /// that has to reach for a private field is worth it here: nothing else fails when this
    /// regresses, it just stops obeying the user.
    /// </summary>
    [Fact]
    public void The_shared_HttpClient_imposes_no_timeout_of_its_own()
    {
        FieldInfo? field = typeof(AiClient).GetField(
            "Http", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var http = (HttpClient)field!.GetValue(null)!;
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
    }

    /// <summary>
    /// The default has to clear a whole guide. 158 pages of the Siglent SDM manual, sent to
    /// Gemini as one PDF, does not finish in two minutes.
    /// </summary>
    [Fact]
    public void The_default_timeout_allows_for_a_whole_guide_in_one_request()
        => Assert.True(new AiConnection().TimeoutSeconds >= 300);

    /// <summary>
    /// Notation the gate used to throw away.
    ///
    /// A real extraction from the Siglent SDM guide had 76 of 218 commands dropped here —
    /// `R?`, `CONFigure:CURRent:{AC|DC}`, `MEASure:{RESistance|FRESistance}?` — every one of
    /// them ordinary SCPI printed in that guide and several already in the shipped catalog.
    /// The rule was "starts with a letter and has two consecutive capitals", which a brace
    /// alternative and a one-letter mnemonic both fail. The user saw a count and lost a third
    /// of the result.
    /// </summary>
    [Theory]
    [InlineData("R?")]                                          // one-letter mnemonic
    [InlineData("CONFigure:CURRent:{AC|DC}")]                   // a choice of nodes
    [InlineData("MEASure:{FREQuency|PERiod}?")]
    [InlineData("CALCulate:LIMit:{LOWer|UPPer}[:DATA]")]
    [InlineData("[SENSe:]VOLTage:{AC|DC}:RANGe")]               // optional root and a choice
    [InlineData("C<n>:BSWV")]                                   // one letter, plus a suffix
    [InlineData("DIG:CHANnel<x>:STATe")]
    [InlineData("CALCulate<n>:MARKer<m>:FUNCtion:POWer:SELect")]
    [InlineData("LOAD[:STATe]")]
    [InlineData("[ADVance:]OCP:STARt")]
    [InlineData("MODE")]
    [InlineData("TRIGger:A:MODE <TriggerMode>")]                // a bare index node
    [InlineData(":MEASure:<function>?")]                        // a placeholder node
    [InlineData("ParaCoPy <destination_channel>,<src_channel>")] // capitals, not adjacent
    [InlineData("POWer:HARMonics:RESults:HAR<1-400>:FREQuency?")] // a suffix range
    [InlineData("MEASure:TEMPerature?[{RTD|THER|DEFault}[,{<type>|DEFault}]]")]
    // R&S writes a suffix as a range or a choice, and its own PDF uses three dots and a
    // typeset ellipsis interchangeably — sometimes on adjacent lines. 176 of 512 commands
    // read out of the FSL manual were dropped over exactly this.
    [InlineData("CALCulate<1|2>:DELTamarker<1...4>:MAXimum:LEFT")]
    [InlineData("CALCulate<1|2>:LIMit<1…8>:ESPectrum:PCLass<1…4>:MINimum")]
    [InlineData("DISPlay[:WINDow<1...4>]:TRACe<1...6>:Y[:SCALe]:RLEVel")]
    public void Real_SCPI_notation_is_accepted(string template)
        => Assert.True(ScpiSyntax.IsValidTemplate(template), template);

    /// <summary>
    /// A suffix that never closes is a line the extractor cut, not a command. The FSL run
    /// produced exactly one — "TRIGger&lt;1|2[:SEQuence]:LEVel[:EXTernal]" — and widening the
    /// character set to admit R&amp;S's ranges must not also admit that.
    /// </summary>
    [Fact]
    public void An_unclosed_suffix_is_still_refused()
        => Assert.False(ScpiSyntax.IsValidTemplate("TRIGger<1|2[:SEQuence]:LEVel[:EXTernal]"));

    /// <summary>
    /// The gate has to pass everything already shipped. It is the same check AI output must
    /// clear, so anything it refuses is a command a user could never extract from a guide the
    /// project has itself transcribed — which is how a third of a real extraction went missing
    /// before this was measured rather than assumed.
    /// </summary>
    [Fact]
    public void Every_shipped_catalog_command_clears_the_gate()
    {
        var refused = Enum.GetValues<InstrumentFamily>()
            .Select(CommandReference.ForFamily)
            .Where(r => r != null)
            .SelectMany(r => r!.Commands)
            .Where(c => !ScpiSyntax.IsValidTemplate(c.Syntax))
            .Select(c => c.Syntax)
            .ToList();

        Assert.True(refused.Count == 0,
            $"{refused.Count} shipped commands the gate would drop: " +
            string.Join(" | ", refused.Take(8)));
    }

    /// <summary>
    /// And the reason the rule was strict in the first place still holds: what comes back
    /// from a model is prose until proven otherwise, and a wrapped PDF line is a parameter
    /// that lost its command.
    /// </summary>
    [Theory]
    [InlineData("The instrument returns the measured voltage.")]
    [InlineData("Set the range to 10 volts")]
    [InlineData("A quick brown fox")]
    [InlineData("This command sets the mode")]
    [InlineData("returns the value")]
    [InlineData("Note: see page 42")]
    [InlineData("{ON|OFF}")]                                    // a parameter, not a command
    [InlineData("<value>")]
    [InlineData("the")]
    [InlineData("")]
    [InlineData("   ")]
    public void Prose_and_orphaned_parameters_are_still_rejected(string notACommand)
        => Assert.False(ScpiSyntax.IsValidTemplate(notACommand), notACommand);
}
