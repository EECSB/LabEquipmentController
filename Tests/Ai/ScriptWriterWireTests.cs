using System.Text.Json.Nodes;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// What the script writer sends and keeps now that it has files and a conversation of kept chats:
/// the files in each provider's own shape, what each provider says it billed, and an answer as a
/// conversation keeps it.
/// </summary>
public class ScriptWriterWireTests
{
    private static readonly AttachmentPart[] Files =
    [
        new(AttachmentKind.Text, "Attached file: scope.png"),
        new(AttachmentKind.Image, "", "image/png", "QUJD", "scope.png"),
        new(AttachmentKind.Pdf, "", "application/pdf", "REVG", "guide.pdf"),
    ];

    private static JsonNode Body(AiProvider provider, IReadOnlyList<AttachmentPart>? parts = null)
        => JsonNode.Parse(AiRequest.Body(
            new AiConnection { Provider = provider, Model = "m" }, "the rules",
            AiPayload.FromText("the request", parts), JsonNode.Parse("{}")!))!;

    private static string Say(JsonNode? node) => node!.GetValue<string>();

    // ------------------------------------------------------------------------------- files

    [Fact]
    public void Gemini_takes_the_files_as_its_own_parts_ahead_of_the_words()
    {
        JsonNode input = Body(AiProvider.Gemini, Files)["input"]!;

        Assert.Equal("text", Say(input[0]!["type"]));
        Assert.Equal("image", Say(input[1]!["type"]));
        Assert.Equal("image/png", Say(input[1]!["mime_type"]));
        Assert.Equal("QUJD", Say(input[1]!["data"]));
        Assert.Equal("document", Say(input[2]!["type"]));
        Assert.Equal("application/pdf", Say(input[2]!["mime_type"]));
        Assert.Contains("the request", Say(input[3]!["text"]));
    }

    [Fact]
    public void Anthropic_takes_the_files_as_its_own_blocks_ahead_of_the_words()
    {
        JsonNode content = Body(AiProvider.Anthropic, Files)["messages"]![0]!["content"]!;

        Assert.Equal("image", Say(content[1]!["type"]));
        Assert.Equal("image/png", Say(content[1]!["source"]!["media_type"]));
        Assert.Equal("document", Say(content[2]!["type"]));
        Assert.Contains("the request", Say(content[3]!["text"]));
    }

    [Fact]
    public void Chat_completions_take_the_files_as_parts_ahead_of_the_words()
    {
        JsonNode content = Body(AiProvider.OpenAiCompatible, Files)["messages"]![0]!["content"]!;

        Assert.Equal("data:image/png;base64,QUJD", Say(content[1]!["image_url"]!["url"]));
        Assert.Equal("guide.pdf", Say(content[2]!["file"]!["filename"]));
        Assert.Contains("the request", Say(content[3]!["text"]));
    }

    /// <summary>Not every server of one's own takes the array form, and a request with nothing attached has no reason to ask it to.</summary>
    [Fact]
    public void With_nothing_attached_chat_completions_are_sent_plain_text()
    {
        JsonNode content = Body(AiProvider.OpenAiCompatible)["messages"]![0]!["content"]!;

        Assert.Contains("the request", content.GetValue<string>());
    }

    [Fact]
    public async Task The_files_go_with_the_request_and_the_model_is_told_the_catalogs_still_rule()
    {
        var client = new Recording("""{"script": "*IDN?"}""");
        var author = new ScriptAuthor(client);
        var file = new AiAttachment("wiring.txt", "text/plain", "CH1 to the divider"u8.ToArray());

        await author.WriteAsync("read it", [new ScriptContextInstrument("", "X", "A,X,1,1", null)], false,
            new AiConnection { Provider = AiProvider.Anthropic }, "k", null, null, null, [file], default);

        AttachmentPart part = Assert.Single(client.Payload!.Parts);
        Assert.Contains("CH1 to the divider", part.Text);
        Assert.Contains("the ones listed for each instrument", client.Instruction);
    }

    [Fact]
    public async Task Without_files_the_model_is_told_nothing_about_files()
    {
        var client = new Recording("""{"script": "*IDN?"}""");

        await new ScriptAuthor(client).WriteAsync("read it", [new ScriptContextInstrument("", "X", "A,X,1,1", null)], false,
            new AiConnection(), "k");

        Assert.Empty(client.Payload!.Parts);
        Assert.DoesNotContain("attached files", client.Instruction);
    }

    // ---------------------------------------------------------------------- what was billed

    /// <summary>Google's own example: thinking is counted apart from what was written, and billed as output.</summary>
    [Fact]
    public void Gemini_says_what_it_billed_with_the_thinking_in_the_output()
    {
        LlmUsage used = AiRequest.UsageFrom(AiProvider.Gemini, """
            {"usage":{"total_cached_tokens":2,"total_input_tokens":7,"total_output_tokens":23,"total_thought_tokens":49,"total_tokens":79}}
            """)!;

        Assert.Equal(7, used.InputTokens);
        Assert.Equal(2, used.CachedInputTokens);
        Assert.Equal(72, used.OutputTokens);
        Assert.Equal(49, used.ReasoningTokens);
        Assert.Equal(1, used.Requests);
    }

    [Fact]
    public void The_other_two_say_it_in_their_own_words()
    {
        Assert.Equal(11, AiRequest.UsageFrom(AiProvider.Anthropic, """{"usage":{"input_tokens":11,"output_tokens":3}}""")!.InputTokens);
        Assert.Equal(13, AiRequest.UsageFrom(AiProvider.OpenAiCompatible, """{"usage":{"prompt_tokens":13,"completion_tokens":3}}""")!.InputTokens);
    }

    [Theory]
    [InlineData("""{"choices":[]}""")]
    [InlineData("<html>not a reply</html>")]
    public void A_reply_that_says_nothing_about_it_is_no_figure(string reply)
        => Assert.Null(AiRequest.UsageFrom(AiProvider.OpenAiCompatible, reply));

    [Fact]
    public async Task What_the_client_says_was_billed_comes_back_with_the_script()
    {
        var client = new Recording("""{"script": "*IDN?"}""") { Billed = new LlmUsage { InputTokens = 100, OutputTokens = 20, Requests = 1 } };

        AuthoredScript written = await new ScriptAuthor(client).WriteAsync(
            "read it", [new ScriptContextInstrument("", "X", "A,X,1,1", null)], false, new AiConnection(), "k");

        Assert.Equal(100, written.Usage!.InputTokens);
    }

    [Fact]
    public async Task A_client_that_cannot_say_leaves_no_figure()
    {
        AuthoredScript written = await new ScriptAuthor(new Recording("""{"script": "*IDN?"}""")).WriteAsync(
            "read it", [new ScriptContextInstrument("", "X", "A,X,1,1", null)], false, new AiConnection(), "k");

        Assert.Null(written.Usage);
    }

    /// <summary>The figure a window adds the conversation and the box to: what goes whatever is asked.</summary>
    [Fact]
    public void The_size_of_a_request_counts_the_script_sent_with_it()
    {
        var author = new ScriptAuthor(new Recording(""));
        ScriptContextInstrument[] bench = [new("", "X", "A,X,1,1", null)];

        int alone = author.PromptCharacters(bench, false, null, null);
        int revising = author.PromptCharacters(bench, false, "*RST\r\n*IDN?", null);

        Assert.True(alone > 500);
        Assert.True(revising > alone);
    }

    // ------------------------------------------------------------------ an answer, as it is kept

    [Fact]
    public void An_answer_comes_back_out_of_its_text_as_it_went_in()
    {
        var written = new WrittenScript("C1:OUTP ON\r\n*IDN?", "Turned the output on.", ["C1:NOPE 1", "C2:NOPE 2"]);

        WrittenScript again = WrittenScript.Read(written.ToText())!;

        Assert.Equal("C1:OUTP ON\r\n*IDN?", again.Script);
        Assert.Equal("Turned the output on.", again.Notes);
        Assert.Equal(["C1:NOPE 1", "C2:NOPE 2"], again.Undocumented);
    }

    [Fact]
    public void An_answer_with_no_note_and_nothing_undocumented_has_neither()
    {
        WrittenScript again = WrittenScript.Read(new WrittenScript("*IDN?", "  ", []).ToText())!;

        Assert.Equal("", again.Notes);
        Assert.Empty(again.Undocumented);
    }

    /// <summary>A chat written out to read another day has the script as its own lines.</summary>
    [Fact]
    public void Written_out_the_script_is_lines_and_not_one_long_string()
        => Assert.Contains("\nC1:OUTP ON\n*IDN?\n", new WrittenScript("C1:OUTP ON\r\n*IDN?", "", []).ToText());

    [Theory]
    [InlineData("A plain answer.")]
    [InlineData("--- The script ---\nnever closed")]
    public void Any_other_text_is_not_an_answer(string text) => Assert.Null(WrittenScript.Read(text));

    private sealed class Recording(string reply) : IAiClient
    {
        public AiPayload? Payload { get; private set; }

        public string Instruction { get; private set; } = "";

        public LlmUsage? Billed { get; init; }

        public Task<string> CompleteAsync(AiConnection connection, string apiKey, string instruction,
                                          AiPayload payload, JsonNode schema, CancellationToken ct = default)
        {
            Instruction = instruction;
            Payload = payload;
            return Task.FromResult(reply);
        }

        public Task<string> CompleteAsync(AiConnection connection, string apiKey, string instruction,
                                          AiPayload payload, JsonNode schema, LlmUsage spent, CancellationToken ct)
        {
            spent.Add(Billed);
            return CompleteAsync(connection, apiKey, instruction, payload, schema, ct);
        }
    }
}
