using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using LabEquipmentController.Web.Bench;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// The page's assistant, through its server: what the relay takes from the page and what it
/// does not. The key, the address and the model are the saved connection's, whatever the page
/// sends; the provider's answer comes back as it came; and a refusal of the relay's own is in
/// the shape the page reads a provider's in.
/// </summary>
public sealed class AiRelayTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "lec-relay-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { /* a temp folder is not worth a failure */ }
    }

    [Fact]
    public async Task A_request_goes_to_the_connections_address_with_its_key_and_its_model()
    {
        var provider = new AiStandIn(AiStandIn.AnthropicText("Hello."));
        var (relay, id) = Relay(provider, new AiOptions { Provider = "Anthropic", Model = "claude-sonnet-5", ApiKey = "sk-saved" });

        var (status, body) = await Forward(relay, id, """{"model":"claude-opus-99","max_tokens":10,"messages":[]}""",
            ("anthropic-beta", "fast-mode-2026-02-01"), ("x-api-key", "sk-from-the-page"), ("Authorization", "Bearer sk-from-the-page"));

        Assert.Equal(200, status);
        Assert.Contains("Hello.", body);
        AiStandIn.Sent sent = Assert.Single(provider.Requests);
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.Uri);
        Assert.Equal("sk-saved", sent.Headers["x-api-key"]);
        Assert.False(sent.Headers.ContainsKey("Authorization"));
        Assert.Equal("fast-mode-2026-02-01", sent.Headers["anthropic-beta"]);
        Assert.Equal("claude-sonnet-5", (string?)sent.Body["model"]);
    }

    [Fact]
    public async Task A_providers_refusal_comes_back_as_it_came()
    {
        var provider = new AiStandIn((_, _) => AiStandIn.Json("""{"error":{"message":"invalid x-api-key"}}""", HttpStatusCode.Unauthorized));
        var (relay, id) = Relay(provider, new AiOptions { Provider = "Anthropic", ApiKey = "sk-wrong" });

        var (status, body) = await Forward(relay, id, """{"messages":[]}""");

        Assert.Equal(401, status);
        Assert.Contains("invalid x-api-key", body);
    }

    [Fact]
    public async Task A_connection_that_is_not_there_is_refused_and_nothing_is_sent()
    {
        var provider = new AiStandIn(AiStandIn.AnthropicText("never"));
        var (relay, _) = Relay(provider, new AiOptions { Provider = "Anthropic", ApiKey = "sk-saved" });

        var (status, body) = await Forward(relay, "no-such-id", """{"messages":[]}""");

        Assert.Equal(404, status);
        Assert.Contains("deleted in AI settings", (string?)JsonNode.Parse(body)!["error"]!["message"]);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task A_connection_with_no_key_is_refused_with_where_to_add_one()
    {
        var provider = new AiStandIn(AiStandIn.AnthropicText("never"));
        var (relay, id) = Relay(provider, new AiOptions { Provider = "Anthropic", ApiKey = "" });

        var (status, body) = await Forward(relay, id, """{"messages":[]}""");

        Assert.Equal(400, status);
        Assert.Contains("AI Connection settings", body);
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task A_provider_this_server_cannot_reach_is_said_in_the_providers_own_error_shape()
    {
        var provider = new AiStandIn((_, _) => throw new HttpRequestException("No such host is known."));
        var (relay, id) = Relay(provider, new AiOptions { Provider = "Gemini", ApiKey = "g-key" });

        var (status, body) = await Forward(relay, id, """{"messages":[]}""");

        Assert.Equal(502, status);
        string said = (string)JsonNode.Parse(body)!["error"]!["message"]!;
        Assert.Contains("generativelanguage.googleapis.com/v1beta/openai/chat/completions", said);
        Assert.Contains("No such host is known.", said);
    }

    // ------------------------------------------------------------------- helpers

    private (AiRelay Relay, string Id) Relay(AiStandIn provider, AiOptions configured)
    {
        var settings = new AiSettingsStore(
            configured,
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = _data })
                .Build(),
            NullLogger<AiSettingsStore>.Instance);
        return (new AiRelay(settings, NullLogger<AiRelay>.Instance, provider.Client()), settings.Book.Items[0].Id);
    }

    private static async Task<(int Status, string Body)> Forward(AiRelay relay, string id, string json, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider(),
        };
        context.Request.Method = "POST";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(json));
        foreach (var (name, value) in headers) context.Request.Headers[name] = value;
        context.Response.Body = new MemoryStream();

        IResult result = await relay.ForwardAsync(id, context.Request, CancellationToken.None);
        await result.ExecuteAsync(context);

        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
