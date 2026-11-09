using System.Net.Http;

namespace LabEquipmentController.Tests;

/// <summary>Who a recording goes to, and what is said to them. The provider's own address only, since a key saved for a gateway is the gateway's.</summary>
public sealed class SpeechTranscriberTests
{
    private static AiConnection OpenAi(string baseUrl = "") => new() { Provider = AiProvider.OpenAiCompatible, BaseUrl = baseUrl, Model = "gpt-5" };
    private static AiConnection Gemini(string baseUrl = "") => new() { Provider = AiProvider.Gemini, BaseUrl = baseUrl, Model = "gemini-3.6-flash" };
    private static AiConnection Claude() => new() { Provider = AiProvider.Anthropic };

    [Fact]
    public void OpenAi_and_Gemini_at_their_own_address_can_and_Claude_cannot()
    {
        Assert.True(SpeechTranscriber.CanTranscribe(OpenAi()));
        Assert.True(SpeechTranscriber.CanTranscribe(OpenAi("https://api.openai.com/")));
        Assert.True(SpeechTranscriber.CanTranscribe(Gemini()));
        Assert.False(SpeechTranscriber.CanTranscribe(Claude()));
    }

    /// <summary>A gateway's key must not go to Google or OpenAI with the recording.</summary>
    [Fact]
    public void A_model_behind_a_gateway_or_on_a_server_of_ones_own_is_not_asked()
    {
        Assert.False(SpeechTranscriber.CanTranscribe(Gemini("https://gateway.example")));
        Assert.False(SpeechTranscriber.CanTranscribe(OpenAi("http://localhost:11434")));
    }

    [Fact]
    public void A_Claude_model_picked_leaves_it_to_the_first_saved_model_that_can()
    {
        AiConnection gemini = Gemini();

        Assert.Same(gemini, SpeechTranscriber.Pick(Claude(), [Claude(), gemini, OpenAi()]));
        Assert.Null(SpeechTranscriber.Pick(Claude(), [Claude()]));
    }

    [Fact]
    public void The_model_picked_goes_first_when_it_can()
    {
        AiConnection picked = OpenAi();

        Assert.Same(picked, SpeechTranscriber.Pick(picked, [Gemini(), picked]));
    }

    [Fact]
    public void The_vocabulary_is_the_benchs_words_and_the_names_once_each()
    {
        string words = SpeechTranscriber.Vocabulary(["SDM3065X", "SCPI", "  ", "SDM3065X"]);

        Assert.StartsWith("SCPI, *IDN?", words);
        Assert.Equal(1, words.Split(", ").Count(w => w == "SDM3065X"));
        Assert.Equal(1, words.Split(", ").Count(w => w == "SCPI"));
    }

    [Fact]
    public async Task OpenAi_is_sent_a_form_with_the_transcription_model_and_the_words_to_expect()
    {
        using HttpRequestMessage request = SpeechTranscriber.OpenAiRequest("sk-key", [1, 2, 3], "SDM3065X");

        Assert.Equal(SpeechTranscriber.OpenAiUrl, request.RequestUri!.ToString());
        Assert.Equal("Bearer sk-key", request.Headers.Authorization!.ToString());
        string body = await request.Content!.ReadAsStringAsync();
        Assert.Contains(SpeechTranscriber.OpenAiModel, body);
        Assert.Contains("SDM3065X", body);
    }

    [Fact]
    public async Task Gemini_is_sent_the_audio_inline_at_its_own_api()
    {
        using HttpRequestMessage request = SpeechTranscriber.GeminiRequest(Gemini(), "g-key", [1, 2, 3], "SDM3065X");

        Assert.StartsWith(SpeechTranscriber.GeminiApi + "gemini-3.6-flash:generateContent", request.RequestUri!.ToString());
        Assert.Equal("g-key", request.Headers.GetValues("x-goog-api-key").Single());
        string body = await request.Content!.ReadAsStringAsync();
        Assert.Contains("audio/wav", body);
        Assert.Contains("AQID", body);
    }

    [Theory]
    [InlineData(AiProvider.OpenAiCompatible, """{"text":" read the meter "}""", "read the meter")]
    [InlineData(AiProvider.Gemini, """{"candidates":[{"content":{"parts":[{"text":"read "},{"text":"the meter"}]}}]}""", "read the meter")]
    [InlineData(AiProvider.Gemini, """{"candidates":[]}""", null)]
    [InlineData(AiProvider.OpenAiCompatible, "not json", null)]
    public void The_words_are_read_out_of_each_providers_answer(AiProvider provider, string json, string? said)
        => Assert.Equal(said, SpeechTranscriber.TextIn(provider, json));

    [Fact]
    public async Task A_model_that_cannot_is_said_and_nothing_is_sent()
    {
        var standIn = new AiStandIn("{}");

        var (text, problem) = await SpeechTranscriber.TranscribeAsync(standIn.Client(), Claude(), "k", [1], "", default);

        Assert.Null(text);
        Assert.Contains("OpenAI or Gemini", problem);
        Assert.Empty(standIn.Requests);
    }
}
