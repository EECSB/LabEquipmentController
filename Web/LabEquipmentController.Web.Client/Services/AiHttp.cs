namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// The client the two AI calls go through: the same pipeline as every other call, and no clock
/// of its own.
///
/// HttpClient gives up at 100 seconds unless told otherwise, and the page's one client was never
/// told otherwise — which is right for everything else it does and wrong for these two. The
/// server gives the model 300 seconds by default and up to 900, and reading a whole programming
/// guide is the request that takes longer than 100. So the browser gave up while the server was
/// still waiting, and an extraction cut off that way said "Stopped." as though Stop had been
/// pressed. The server's own client had exactly this bug (see Core's <c>AiClient</c>), and the
/// fix there is the fix here: no timeout on the client, and whatever bounds a request bounds
/// that request.
///
/// A type of its own rather than a second <see cref="HttpClient"/> in the container, because
/// the container hands out one HttpClient and every other call should go on getting that one.
/// </summary>
public sealed class AiHttp(HttpMessageHandler handler, Uri baseAddress)
{
    public HttpClient Http { get; } = new(handler)
    {
        BaseAddress = baseAddress,
        Timeout = Timeout.InfiniteTimeSpan,
    };
}
