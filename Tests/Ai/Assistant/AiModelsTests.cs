using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// What each model takes per request — effort levels and fast mode — read from its version, with
/// LEC's three providers mapped onto GDS Viewer's four: an OpenAI-compatible connection is OpenAI's
/// only at OpenAI's own address, and anywhere else a server of one's own that is offered nothing.
/// </summary>
public class AiModelsTests
{
    private const string OpenAi = "https://api.openai.com";
    private const string Ollama = "http://localhost:11434";

    private static AiConnection Connection(AiProvider provider, string model, string baseUrl = "")
        => new() { Provider = provider, Model = model, BaseUrl = baseUrl };

    [Theory]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-5-mini", "", true)]
    [InlineData(AiProvider.OpenAiCompatible, "o3", OpenAi, true)]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-6.1-sol", "", true)]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-4o-mini", "", false)]
    [InlineData(AiProvider.OpenAiCompatible, "o3", Ollama, false)]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-5", "https://openrouter.ai/api", false)]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-5", "https://api.openai.com.example.net", false)]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-5", "http://api.openai.com", false)]
    [InlineData(AiProvider.Gemini, "gemini-2.5-flash", "", true)]
    [InlineData(AiProvider.Gemini, "gemini-3.6-flash", "", true)]
    [InlineData(AiProvider.Gemini, "gemini-2.0-flash", "", false)]
    [InlineData(AiProvider.Anthropic, "claude-opus-5-5", "", true)]
    [InlineData(AiProvider.Anthropic, "claude-haiku-4-5", "", false)]
    [InlineData(AiProvider.Anthropic, "claude-sonnet-4-5", "", false)]
    public void An_effort_is_offered_only_where_it_is_understood(AiProvider provider, string model, string baseUrl, bool expected)
        => Assert.Equal(expected, AiModels.SupportsEffort(Connection(provider, model, baseUrl)));

    /// <summary>The model a request names is the one asked about: a connection left blank runs its provider's default, and is offered that model's levels.</summary>
    [Fact]
    public void A_model_left_blank_is_offered_what_its_default_takes()
    {
        Assert.Equal("low medium high xhigh max", string.Join(" ", AiModels.EffortLevels(Connection(AiProvider.Anthropic, ""))));
        Assert.Equal("low medium high", string.Join(" ", AiModels.EffortLevels(Connection(AiProvider.Gemini, ""))));
        Assert.Empty(AiModels.EffortLevels(Connection(AiProvider.OpenAiCompatible, "")));
        Assert.Empty(AiModels.EffortLevels(null));
    }

    [Theory]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-5-mini", "medium")]
    [InlineData(AiProvider.OpenAiCompatible, "o3", "medium")]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-5.1", "none")]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-5.2-codex", "none")]
    [InlineData(AiProvider.OpenAiCompatible, "gpt-4o-mini", "")]
    [InlineData(AiProvider.Gemini, "gemini-3.8-flash", "high")]
    [InlineData(AiProvider.Gemini, "gemini-3-pro", "high")]
    [InlineData(AiProvider.Gemini, "gemini-2.5-flash", "as needed")]
    [InlineData(AiProvider.Gemini, "gemini-2.5-flash-lite", "none")]
    [InlineData(AiProvider.Anthropic, "claude-opus-5-5", "medium")]
    [InlineData(AiProvider.Anthropic, "claude-haiku-5-5", "medium")]
    [InlineData(AiProvider.Anthropic, "claude-opus-5", "high")]
    [InlineData(AiProvider.Anthropic, "claude-sonnet-5-5", "high")]
    [InlineData(AiProvider.Anthropic, "claude-haiku-4-5", "")]
    public void What_default_means_is_named_by_the_models_family(AiProvider provider, string model, string expected)
        => Assert.Equal(expected, AiModels.DefaultEffort(Connection(provider, model)));

    /// <summary>Claude's levels by version: three from Opus 4.5, max with Opus and Sonnet 4.6, xhigh with Opus 4.7 and every model since — a dated id read by its version, not its date.</summary>
    [Theory]
    [InlineData("claude-opus-4-5-20251101", "low medium high")]
    [InlineData("claude-opus-4-6", "low medium high max")]
    [InlineData("claude-sonnet-4-6", "low medium high max")]
    [InlineData("claude-opus-4-7", "low medium high xhigh max")]
    [InlineData("claude-opus-5-5", "low medium high xhigh max")]
    [InlineData("claude-sonnet-5", "low medium high xhigh max")]
    [InlineData("claude-fable-5-1", "low medium high xhigh max")]
    [InlineData("claude-haiku-5-5", "low medium high xhigh max")]
    [InlineData("claude-sonnet-4-20250514", "")]
    [InlineData("not-a-claude", "")]
    public void Claude_is_offered_the_levels_its_version_takes(string model, string expected)
        => Assert.Equal(expected, string.Join(" ", AiModels.EffortLevels(Connection(AiProvider.Anthropic, model))));

    /// <summary>Fast mode is Opus 4.8 and Opus 5 on, and Anthropic's alone — Opus 4.7's was taken away.</summary>
    [Theory]
    [InlineData(AiProvider.Anthropic, "claude-opus-5-5", true)]
    [InlineData(AiProvider.Anthropic, "claude-opus-5", true)]
    [InlineData(AiProvider.Anthropic, "claude-opus-4-8", true)]
    [InlineData(AiProvider.Anthropic, "claude-opus-4-7", false)]
    [InlineData(AiProvider.Anthropic, "claude-sonnet-5-5", false)]
    [InlineData(AiProvider.Anthropic, "claude-fable-5-1", false)]
    [InlineData(AiProvider.OpenAiCompatible, "claude-opus-5-5", false)]
    public void Fast_mode_is_offered_only_on_the_models_that_have_it(AiProvider provider, string model, bool expected)
        => Assert.Equal(expected, AiModels.SupportsFast(Connection(provider, model)));

    /// <summary>A level chosen for one model is dropped for the next that does not take it, rather than sent to be refused.</summary>
    [Theory]
    [InlineData("claude-opus-5-5", "xhigh", "xhigh")]
    [InlineData("claude-opus-4-6", "xhigh", "")]
    [InlineData("claude-sonnet-4-5", "high", "")]
    [InlineData("claude-opus-5-5", "HIGH", "")]
    [InlineData("claude-opus-5-5", null, "")]
    public void A_level_the_model_does_not_take_is_dropped(string model, string? chosen, string expected)
        => Assert.Equal(expected, AiModels.ResolveEffort(Connection(AiProvider.Anthropic, model), chosen));
}
