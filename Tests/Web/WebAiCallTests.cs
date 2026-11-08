using System.Net;
using System.Net.Http.Json;
using System.Text;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Client.Services;
using Xunit;

namespace LabEquipmentController.Tests;

/// <summary>
/// The browser's two AI calls, as far as the wire: which client carries them, how long it waits,
/// and what it says when what comes back is not a reply.
/// </summary>
/// <remarks>
/// The page used one client for everything, at HttpClient's default of 100 seconds. The server
/// gives the model 300 by default and up to 900, so a long extraction was cut off by the browser
/// while the server was still waiting on the model, and said "Stopped." as though Stop had been
/// pressed. And a reply that was not one — a proxy's 502 page — was read as JSON, and the parse
/// error escaped the script writer's button. A fake handler stands where the server would.
/// </remarks>
public class WebAiCallTests
{
    private static readonly Uri Base = new("http://bench.test/");

    private static readonly AiScriptRequest Ask = new("read the meter", ["s1"], false, null, null);
    private static readonly AiExtractRequest Guide = new("guide.pdf", Convert.ToBase64String("%PDF"u8.ToArray()));

    [Fact]
    public void The_ai_client_has_no_clock_of_its_own()
    {
        var ai = new AiHttp(new Handler((_, _) => Task.FromResult(new HttpResponseMessage())), Base);

        // Whatever bounds an AI request is the request's own clock, and the server's. A client
        // timeout fires first on anything longer, and cannot say what it was waiting for.
        Assert.Equal(Timeout.InfiniteTimeSpan, ai.Http.Timeout);
    }

    [Fact]
    public async Task The_two_ai_calls_go_through_the_ai_client_and_nothing_else_does()
    {
        var everyday = new Handler((_, _) => Json(new AiStatus(true, "Gemini", "m", "", 300, null, false,
            null, [], "Default", [], [], "c1")));
        var models = new Handler((req, _) => req.RequestUri!.AbsolutePath.EndsWith("/script")
            ? Json(new AiScriptReply("*IDN?", [], null))
            : Json(new AiExtractReply(0, 0, [], "t", null)));
        var bench = Bench(everyday, models);

        await bench.AiStatusAsync();
        await bench.AiScriptAsync(Ask, TimeSpan.FromMinutes(1));
        await bench.AiExtractAsync(Guide);

        Assert.Equal(new[] { "/api/ai" }, everyday.Paths);
        Assert.Equal(new[] { "/api/ai/script", "/api/ai/extract" }, models.Paths);
    }

    [Fact]
    public void The_writer_waits_longer_than_the_server_gives_the_model()
    {
        // Longer, so the server's own "did not answer within 300s" is what arrives.
        Assert.True(BenchClient.ScriptWait(300) > TimeSpan.FromSeconds(300));

        // The server floors the limit at ten seconds; a page waiting less would beat it to it.
        Assert.True(BenchClient.ScriptWait(1) > TimeSpan.FromSeconds(10));

        // Nothing to read the limit from: wait out the most the settings allow.
        Assert.True(BenchClient.ScriptWait(null) > TimeSpan.FromSeconds(900));
    }

    [Fact]
    public async Task A_script_the_server_never_answers_comes_back_as_a_reply_that_says_so()
    {
        var silent = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });

        var reply = await Bench(silent, silent).AiScriptAsync(Ask, TimeSpan.FromMilliseconds(200));

        // A reply, not an exception: the window shows it as it shows any other failure.
        Assert.NotNull(reply.Error);
        Assert.Contains("did not reply within", reply.Error);
        Assert.Equal("", reply.Script);
    }

    [Fact]
    public async Task A_stop_is_let_through_rather_than_said_as_a_failure()
    {
        var silent = new Handler(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage();
        });
        var bench = Bench(silent, silent);

        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bench.AiScriptAsync(Ask, TimeSpan.FromMinutes(1), stop.Token));

        using var stopToo = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => bench.AiExtractAsync(Guide, stopToo.Token));
    }

    [Fact]
    public async Task A_reply_that_is_not_a_reply_is_said_by_its_status()
    {
        var proxy = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("<html><body>502 Bad Gateway</body></html>", Encoding.UTF8, "text/html"),
        }));
        var bench = Bench(proxy, proxy);

        var script = await bench.AiScriptAsync(Ask, TimeSpan.FromMinutes(1));
        var extract = await bench.AiExtractAsync(Guide);

        Assert.Contains("502", script.Error);
        Assert.Contains("502", extract.Error);
        Assert.DoesNotContain("invalid start of a value", script.Error);
    }

    [Fact]
    public async Task A_success_whose_body_cannot_be_read_says_so_rather_than_throwing()
    {
        var odd = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>a login page, say</html>", Encoding.UTF8, "text/html"),
        }));
        var bench = Bench(odd, odd);

        var script = await bench.AiScriptAsync(Ask, TimeSpan.FromMinutes(1));
        var extract = await bench.AiExtractAsync(Guide);

        Assert.Contains("not with a reply", script.Error);
        Assert.Contains("not with a reply", extract.Error);
    }

    // ---------------------------------------------------------------------------- helpers

    private static BenchClient Bench(Handler everyday, Handler models)
        => new(new HttpClient(everyday) { BaseAddress = Base }, new AiHttp(models, Base));

    private static Task<HttpResponseMessage> Json<T>(T body)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });

    /// <summary>Answers as it is told, and keeps the path of everything it was asked.</summary>
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> answer)
        : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Paths) Paths.Add(request.RequestUri!.AbsolutePath);
            return answer(request, ct);
        }
    }
}
