using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LabEquipmentController;

/// <summary>
/// What requests were billed for, in tokens: one answer's, a whole run's, or everything a
/// provider has sent.
///
/// **Three of the six are parts, not additions** — the shape ConfigurationRuleEditor keeps, and
/// GDS Viewer after it. <see cref="InputTokens"/> is everything read, and
/// <see cref="CachedInputTokens"/> and <see cref="CacheWriteTokens"/> are the parts of it read
/// from a provider's cache and written into one, each billed at a rate of its own;
/// <see cref="OutputTokens"/> is everything written, and <see cref="ReasoningTokens"/> the part of
/// it that was thinking. Priced in <see cref="LlmPrice.Cost"/>.
///
/// **The JSON names are pinned.** A kept conversation carries what it cost, and a property
/// renamed here would read an earlier version's as nothing spent.
/// </summary>
public sealed class LlmUsage
{
    [JsonPropertyName("InputTokens")]
    public long InputTokens { get; set; }

    /// <summary>Part of <see cref="InputTokens"/>: read back from the provider's cache, at a tenth of the rate or less.</summary>
    [JsonPropertyName("CachedInputTokens")]
    public long CachedInputTokens { get; set; }

    /// <summary>Part of <see cref="InputTokens"/>: put into Anthropic's cache, at a quarter over the rate. Nobody else reports writes.</summary>
    [JsonPropertyName("CacheWriteTokens")]
    public long CacheWriteTokens { get; set; }

    [JsonPropertyName("OutputTokens")]
    public long OutputTokens { get; set; }

    /// <summary>Part of <see cref="OutputTokens"/>: the thinking rather than the answer, billed the same.</summary>
    [JsonPropertyName("ReasoningTokens")]
    public long ReasoningTokens { get; set; }

    /// <summary>How many answers said what they cost. A run of tool calls is several billed requests.</summary>
    [JsonPropertyName("Requests")]
    public int Requests { get; set; }

    /// <summary>
    /// False until an answer has said what it cost. A server of one's own may never say, and then
    /// nothing is shown rather than a zero that reads as free.
    /// </summary>
    [JsonIgnore]
    public bool Reported => Requests > 0;

    public void Add(LlmUsage? more)
    {
        if (more is null) return;

        InputTokens += more.InputTokens;
        CachedInputTokens += more.CachedInputTokens;
        CacheWriteTokens += more.CacheWriteTokens;
        OutputTokens += more.OutputTokens;
        ReasoningTokens += more.ReasoningTokens;
        Requests += more.Requests;
    }

    public LlmUsage Copy()
    {
        var copy = new LlmUsage();
        copy.Add(this);
        return copy;
    }

    /// <summary>What was added since <paramref name="before"/> was copied from this — one run's share of a provider's total.</summary>
    public LlmUsage Since(LlmUsage before) => new()
    {
        InputTokens = InputTokens - before.InputTokens,
        CachedInputTokens = CachedInputTokens - before.CachedInputTokens,
        CacheWriteTokens = CacheWriteTokens - before.CacheWriteTokens,
        OutputTokens = OutputTokens - before.OutputTokens,
        ReasoningTokens = ReasoningTokens - before.ReasoningTokens,
        Requests = Requests - before.Requests,
    };

    /// <summary>
    /// What one Messages API answer says it cost, or null when it says nothing.
    ///
    /// **Anthropic's input_tokens leaves its cache out**: it counts what came after the last place
    /// the cache was read or written, and cache_read_input_tokens and cache_creation_input_tokens
    /// count the rest. So everything read is the three together, which is the shape the other
    /// providers report it in — and taking input_tokens alone for the total would leave every
    /// cache read and write off the bill.
    /// </summary>
    public static LlmUsage? FromAnthropic(JsonElement root)
    {
        if (!TryUsage(root, out JsonElement usage)) return null;

        long fresh = Count(usage, "input_tokens");
        long read = Count(usage, "cache_read_input_tokens");
        long written = Count(usage, "cache_creation_input_tokens");

        return new LlmUsage
        {
            InputTokens = fresh + read + written,
            CachedInputTokens = read,
            CacheWriteTokens = written,
            OutputTokens = Count(usage, "output_tokens"),
            Requests = 1,
        };
    }

    /// <summary>
    /// What one chat-completions answer says it cost, or null when it says nothing — which a
    /// server of one's own may.
    ///
    /// prompt_tokens counts everything read, its cached part in prompt_tokens_details;
    /// completion_tokens everything written, its reasoning in completion_tokens_details.
    ///
    /// **Gemini, through Google's compatible endpoint, may leave its thinking out of
    /// completion_tokens** and count it in total_tokens alone, so what total_tokens holds beyond
    /// the other two is taken for thinking, which Google bills as output. Where it holds nothing
    /// more, nothing is added. That is how the endpoint answers as people using it report it, not
    /// anything Google documents for it — its page says nothing of the fields (GDS Viewer read it
    /// 2026-10-29) — so a reasoning count said beside the surplus is taken for the same thinking
    /// said twice, and the larger of the two is the thinking, never their sum.
    /// </summary>
    public static LlmUsage? FromOpenAi(JsonElement root)
    {
        if (!TryUsage(root, out JsonElement usage)) return null;

        long prompt = Count(usage, "prompt_tokens");
        long completion = Count(usage, "completion_tokens");
        long cached = usage.TryGetProperty("prompt_tokens_details", out JsonElement read)
                   && read.ValueKind == JsonValueKind.Object
            ? Count(read, "cached_tokens")
            : 0;
        long reasoning = usage.TryGetProperty("completion_tokens_details", out JsonElement written)
                      && written.ValueKind == JsonValueKind.Object
            ? Count(written, "reasoning_tokens")
            : 0;

        long unsaid = Count(usage, "total_tokens") - prompt - completion;
        if (unsaid > 0)
        {
            completion += unsaid;
            reasoning = Math.Max(reasoning, unsaid);
        }

        return new LlmUsage
        {
            InputTokens = prompt,
            CachedInputTokens = cached,
            OutputTokens = completion,
            ReasoningTokens = reasoning,
            Requests = 1,
        };
    }

    /// <summary>
    /// What one Gemini Interactions answer says it cost, or null when it says nothing.
    ///
    /// Thinking is counted apart from what was written, as Google's own example adds up (7 in, 23
    /// out, 49 thinking, 79 in all), and billed as output, so here it is part of the output.
    /// </summary>
    public static LlmUsage? FromInteractions(JsonElement root)
    {
        if (!TryUsage(root, out JsonElement usage)) return null;

        long thought = Count(usage, "total_thought_tokens");

        return new LlmUsage
        {
            InputTokens = Count(usage, "total_input_tokens"),
            CachedInputTokens = Count(usage, "total_cached_tokens"),
            OutputTokens = Count(usage, "total_output_tokens") + thought,
            ReasoningTokens = thought,
            Requests = 1,
        };
    }

    private static bool TryUsage(JsonElement root, out JsonElement usage)
    {
        usage = default;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("usage", out usage)
            && usage.ValueKind == JsonValueKind.Object;
    }

    private static long Count(JsonElement owner, string name)
        => owner.TryGetProperty(name, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? Math.Max(0, number)
            : 0;
}
