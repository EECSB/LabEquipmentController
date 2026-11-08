using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>What one completion came back with: the text the model wrote, or why there is none.</summary>
public readonly struct LlmResult
{
    // Fields rather than auto-properties so that default(LlmResult) still answers with empty
    // strings rather than nulls a caller would have to know to expect.
    private readonly string? _text;
    private readonly string? _error;

    private LlmResult(string text, string error, bool isError, bool isPaused)
    {
        _text = text;
        _error = error;
        IsError = isError;
        IsPaused = isPaused;
    }

    /// <summary>The answer — or, for a pause, the question <see cref="LlmProviders.RoundsPaused"/> asks. Empty for a failure.</summary>
    public string Text => _text ?? "";

    /// <summary>Why there is no answer, as a sentence for the person. Empty unless <see cref="IsError"/>.</summary>
    public string Error => _error ?? "";

    public bool IsError { get; }

    /// <summary>
    /// A run of tool calls that reached its limit of rounds still getting somewhere: not an
    /// answer, and not an error either. <see cref="Text"/> says so, and asks whether to go on.
    /// </summary>
    public bool IsPaused { get; }

    public static LlmResult Success(string text) => new(text, "", isError: false, isPaused: false);

    public static LlmResult Failure(string error) => new("", error, isError: true, isPaused: false);

    public static LlmResult Paused(string note) => new(note, "", isError: false, isPaused: true);
}

/// <summary>
/// A model the assistant can talk to: one implementation per family of API — Anthropic's
/// Messages API (<see cref="AnthropicProvider"/>), and OpenAI's chat completions, which Google
/// and most servers of one's own speak as well (<see cref="OpenAiProvider"/>).
///
/// GDS Viewer's design, which is GraphDBViewer's, with the names kept so a fix found in one
/// project can be carried to the others by name. What is different here is where a request
/// goes. A provider builds the body and hands it to an <see cref="ILlmEndpoint"/>, which
/// addresses and signs it: the desktop holds the key and sends straight to the provider, and the
/// web page holds none and sends through the bench's own server. So no provider here ever sees
/// a key, and none can tell which way its requests went.
///
/// Bodies are built as JSON nodes rather than serialised from anonymous objects, as GDS Viewer
/// builds them and for its reason: a trimmed WebAssembly build — which the web page is, and it
/// references this library — cannot promise that reflection over a type nobody names survives.
/// </summary>
public interface ILlmProvider
{
    /// <summary>The model it asks for, as it is sent — what its spend is kept under.</summary>
    string Model { get; }

    /// <summary>
    /// Everything its requests were billed for, added as each answer comes back rather than when
    /// a run ends — so a run stopped or failed partway still has what it spent counted. A caller
    /// wanting one run's share copies this before and takes <see cref="LlmUsage.Since"/> after.
    /// </summary>
    LlmUsage Spent { get; }

    /// <summary>What its tokens cost, or null when no rate is known for the model — see <see cref="AiPricing.For"/>.</summary>
    LlmPrice? Price { get; }

    /// <summary>A plain answer to a conversation, with no tools.</summary>
    Task<LlmResult> CompleteAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation, CancellationToken ct);

    /// <summary>
    /// The same conversation with the runner's tools offered, running each call the model makes
    /// and handing back what it answered, until the model replies in words or
    /// <paramref name="maxRounds"/> runs out — the person's setting, where the chat passes one.
    ///
    /// The runner is called from the loop's own continuations, which do not come back to the
    /// caller's synchronisation context — this is library code, and awaits as library code does.
    /// A host that shows a card when a call arrives marshals to its own thread to do it.
    /// </summary>
    Task<LlmResult> CompleteWithToolsAsync(string systemPrompt, IReadOnlyList<LlmMessage> conversation,
                                           ILlmToolRunner tools, CancellationToken ct,
                                           int maxRounds = LlmProviders.DefaultToolRounds);
}

/// <summary>
/// Builds the right <see cref="ILlmProvider"/> for a connection, and holds what both families
/// share: the limits on a run of tool calls, the clock on a request, and the sentences for what
/// goes wrong on the way. GDS Viewer's <c>LlmProviderFactory</c>, named here for what it holds
/// rather than for the one thing it builds.
/// </summary>
public static class LlmProviders
{
    /// <summary>
    /// How many rounds of tool calls one message may take before the run is paused for the
    /// person to carry on.
    ///
    /// A pause, not an error. GDS Viewer's first cap was ten rounds and a red "kept calling tools
    /// without answering", and it fired on a model doing exactly what it was asked: a cell drawn,
    /// labelled, looked at and checked is ten rounds of calls that all worked — as a supply set,
    /// its output switched on, a meter read and a trace captured would be here. What a count
    /// still guards against is spend, since every round sends the whole conversation again, so it
    /// stays — higher — and ends the run with a question rather than a failure. A model that is
    /// actually stuck is <see cref="ToolProgress"/>'s to stop, long before this.
    ///
    /// Thirty, where GDS Viewer starts it; the person's to change, where the chat passes its own.
    /// </summary>
    public const int DefaultToolRounds = 30;

    /// <summary>What a run says when its rounds run out with the calls still going somewhere: the question a Continue answers.</summary>
    public static string RoundsPaused(int rounds)
        => $"The assistant reached the set limit of {rounds} rounds of tool calls, and is paused. "
         + $"Nothing went wrong — continue for another {rounds} rounds?";

    /// <summary>
    /// How many rounds in a row may get nowhere before the run is stopped as stuck: every call in
    /// them refused, or one already made and answered the same way. Three, so a model that tries
    /// one thing that fails and then something else is left to find its way, and one going round
    /// in circles is stopped within a few requests.
    /// </summary>
    public const int MaxStuckRounds = 3;

    /// <summary>What a run says when <see cref="MaxStuckRounds"/> runs out — the one way a run of calls is an error.</summary>
    public static readonly string RoundsStuck =
        $"The model went {MaxStuckRounds} rounds of tool calls without getting anywhere — each call "
      + "refused, or one it had already made — and was stopped.";

    /// <summary>
    /// How long one request may take before it is given up on.
    ///
    /// The client's own timeout is off — see <see cref="NewHttpClient"/> — so this is the only
    /// clock, and it belongs to each request rather than to the client. Five minutes, as GDS
    /// Viewer has it: longer than a model takes to think about anything the assistant asks, and
    /// short enough that a request nobody is going to answer does not hold the window until it
    /// is closed.
    /// </summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The provider for a connection: Anthropic's own API for an Anthropic connection, and chat
    /// completions for the other two — Gemini at Google's OpenAI-compatible endpoint, and an
    /// OpenAI-compatible server as itself.
    ///
    /// <paramref name="effort"/> and <paramref name="fast"/> are chosen per request and never
    /// saved, and each provider drops whichever its model does not take — see
    /// <see cref="AiModels"/>. Fast mode is Anthropic's alone.
    /// </summary>
    public static ILlmProvider Create(HttpClient http, ILlmEndpoint endpoint, AiConnection connection,
                                      string effort = "", bool fast = false)
        => connection.Provider == AiProvider.Anthropic
            ? new AnthropicProvider(http, endpoint, connection, effort, fast)
            : new OpenAiProvider(http, endpoint, connection, effort);

    /// <summary>
    /// The client the providers send with — **with no timeout of its own**.
    ///
    /// HttpClient gives up after a hundred seconds unless told otherwise, and a model writing a
    /// long answer, or thinking hard, can take longer than that. This project found it first:
    /// <see cref="AiClient"/>'s datasheet extraction was cut off at exactly 100 s with the
    /// provider still working, and GDS Viewer took the fix from there. Each request carries
    /// <see cref="RequestTimeout"/> instead, which says which request it was when it fires.
    /// </summary>
    public static HttpClient NewHttpClient() => new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>
    /// Sends a request and reads what came back, turning what can go wrong on the way into a
    /// sentence that names <paramref name="label"/>.
    ///
    /// **The status is read, not only the body.** A provider that refuses says why in its body
    /// most of the time, and that is the sentence worth showing — a key that is wrong, a model
    /// that does not exist, a quota spent. But a gateway in between answers with a page of HTML,
    /// and GraphDBViewer said "Could not parse model response" for one, which names the wrong
    /// problem. So a failure that says nothing readable is reported by its status.
    ///
    /// **A request that never arrives is said as one**, with the address it was going to and what
    /// to check: a server of one's own that is not running is the likeliest failure there is. No
    /// page here calls a provider itself — the web page goes through the bench's own server — so
    /// GDS Viewer's note about CORS has nothing to say here and is left out.
    ///
    /// **A Stop is let through as it is**, so the window can tell it from a failure. A person's
    /// Stop and this clock firing are the same exception, and the filter on
    /// <paramref name="ct"/> is what tells them apart: without it a timeout is reported as "you
    /// stopped it", the one message guaranteed to stop anybody investigating — which is what the
    /// web page here said of its own AI calls until they were given a clock of their own.
    ///
    /// <paramref name="timeout"/> is <see cref="RequestTimeout"/> unless a caller has a reason to
    /// give one; the tests give a short one so a slow server can be waited out in milliseconds.
    /// </summary>
    internal static async Task<(string? Body, string? Problem)> SendAsync(HttpClient http, HttpRequestMessage request,
                                                                         string label, CancellationToken ct,
                                                                         TimeSpan? timeout = null)
    {
        TimeSpan limit = timeout ?? RequestTimeout;

        using HttpRequestMessage sending = request;
        using var clock = CancellationTokenSource.CreateLinkedTokenSource(ct);
        clock.CancelAfter(limit);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(sending, clock.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, $"{label} did not answer within {Seconds(limit)}s.");
        }
        catch (HttpRequestException failed)
        {
            string address = sending.RequestUri?.GetLeftPart(UriPartial.Authority) ?? "the server";
            return (null, $"Could not reach {address}: {failed.Message.TrimEnd('.')}. "
                        + "Check the address, and that the server is running.");
        }

        using (response)
        {
            string body;
            try
            {
                body = await response.Content.ReadAsStringAsync(clock.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return (null, $"{label} did not finish answering within {Seconds(limit)}s.");
            }

            if (response.IsSuccessStatusCode) return (body, null);

            string status = $"{(int)response.StatusCode} {response.ReasonPhrase}".Trim();

            if (ErrorIn(body) is string said) return (null, $"{label} answered {status}: {said}");

            return response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? (null, $"{label} answered {status}. The key may be wrong, or not allowed to use this model.")
                : (null, $"{label} answered {status}.");
        }
    }

    /// <summary>
    /// The message in an error body, or null when there is none to read.
    ///
    /// Every provider here puts it at <c>error.message</c>. Google's OpenAI-compatible endpoint
    /// sometimes wraps the object in an array — <c>[{ "error": … }]</c> — which is read through.
    /// </summary>
    internal static string? ErrorIn(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0) root = root[0];

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("error", out JsonElement error))
                return null;

            if (error.ValueKind == JsonValueKind.Object
             && error.TryGetProperty("message", out JsonElement message)
             && message.ValueKind == JsonValueKind.String)
                return message.GetString();

            return error.ValueKind == JsonValueKind.String ? error.GetString() : error.ToString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Seconds(TimeSpan limit) => ((int)Math.Ceiling(limit.TotalSeconds)).ToString();
}

/// <summary>
/// Whether a run of tool calls is getting anywhere: what tells a model at work from a stuck one,
/// where a count of rounds could not — see <see cref="LlmProviders.DefaultToolRounds"/>.
///
/// **A round gets somewhere when one of its calls worked and was not a repeat**: a call already
/// made, with the same arguments, that answered the same — the same words and the same picture.
/// A look after a change is not a repeat, because the picture is not the same; a second look at
/// nothing new is. A refusal is never progress.
///
/// **Nor is a call a person declined**, though the model is told it as a result rather than an
/// error, so that it does not go looking for another way to the same end. Counted as progress, it
/// let a model asking for one change after another run to the cap with every card answered no —
/// GraphDBViewer's audit found it, and GDS Viewer took the fix — so
/// <see cref="LlmToolOutput.IsDeclined"/> is skipped here with the errors.
/// </summary>
public sealed class ToolProgress
{
    private readonly HashSet<string> _seen = new();
    private int _stuckRounds;

    /// <summary>
    /// Counts one round's calls, and says whether the run has now gone
    /// <see cref="LlmProviders.MaxStuckRounds"/> rounds in a row without getting anywhere.
    /// </summary>
    public bool IsStuckAfter(IReadOnlyList<(LlmToolCall Call, LlmToolOutput Output)> round)
    {
        bool anythingNew = false;

        foreach (var (call, output) in round)
        {
            if (output.IsError || output.IsDeclined) continue;

            // A picture is told apart by its length and its hash rather than kept whole: thirty
            // rounds of screenshots held only to be compared would be megabytes for a yes or no.
            string picture = output.Png is string png ? $"{png.Length}:{png.GetHashCode()}" : "";

            if (_seen.Add(string.Join('\u0000', call.Name, call.ArgumentsJson, output.Text, picture)))
                anythingNew = true;
        }

        _stuckRounds = anythingNew ? 0 : _stuckRounds + 1;
        return _stuckRounds >= LlmProviders.MaxStuckRounds;
    }
}
