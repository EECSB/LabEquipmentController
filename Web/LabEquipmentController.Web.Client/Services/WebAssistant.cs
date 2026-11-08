using System.Text;
using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// The page's way to a provider: through its own server, which signs the request with the key
/// the page never has (<c>AiRelay</c> on the server). The provider builds the same body it would
/// send direct; only the address differs, and it names a saved connection rather than a place.
/// </summary>
public sealed class RelayLlmEndpoint(string connectionId) : ILlmEndpoint
{
    public HttpRequestMessage Post(string body, IReadOnlyDictionary<string, string>? headers = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/ai/relay/{Uri.EscapeDataString(connectionId)}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
                request.Headers.TryAddWithoutValidation(name, value);
        }
        return request;
    }
}

/// <summary>
/// The bench as the page's assistant sees it: the server's open sessions, reached through the
/// same calls the consoles make, so what the assistant sends is what a console would have sent
/// and is serialised with everything else on that connection.
/// </summary>
public sealed class WebBench(BenchClient bench) : IAssistantBench
{
    public async Task<IReadOnlyList<BenchInstrument>> ListAsync(CancellationToken ct)
    {
        IReadOnlyList<SessionDto> sessions = await bench.SessionsAsync();
        return [.. sessions.Select(s => new BenchInstrument(s.Id, s.Identity, s.Address, s.TransportDetail, s.Driven))];
    }

    // The curated catalog, which ships in the page with Core. A datasheet's extracted commands
    // live on the server and are not offered here yet.
    public Task<CommandReference?> CatalogAsync(BenchInstrument instrument, CancellationToken ct)
        => Task.FromResult(CommandReference.ForIdentity(instrument.Identity));

    public async Task<string> QueryAsync(BenchInstrument instrument, string command, CancellationToken ct)
    {
        CommandReply reply = await bench.SendAsync(instrument.Key, command);
        if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
        return reply.Reply ?? "";
    }

    public async Task SendAsync(BenchInstrument instrument, string command, CancellationToken ct)
    {
        CommandReply reply = await bench.SendAsync(instrument.Key, command);
        if (reply.Error is not null) throw new InvalidOperationException(reply.Error);
    }

    public async Task<byte[]> CaptureScreenAsync(BenchInstrument instrument, CancellationToken ct)
    {
        ScreenshotDto shot = await bench.ScreenshotAsync(instrument.Key);
        if (shot.Error is not null) throw new InvalidOperationException(shot.Error);
        return Convert.FromBase64String(shot.Base64);
    }
}
