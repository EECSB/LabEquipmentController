using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LabEquipmentController;

/// <summary>
/// What a connection's model takes per request: the effort levels it reads, and whether it has a
/// fast mode — decided from the model's version number rather than a list of names, so a model
/// released after this was written lands in its family instead of quietly missing out.
///
/// GDS Viewer keeps these on its saved model (<c>LlmConnection.EffortLevels</c>,
/// <c>SupportsEffort</c>, <c>SupportsFast</c>, <c>DefaultEffort</c>). Here they read an
/// <see cref="AiConnection"/> and stand apart from it, because they are not settings: an effort
/// and fast mode are chosen per request and never saved, so a level chosen for one model is
/// never written out against the next. That is also why <see cref="AiConnection.Effort"/> — the
/// datasheet reader's older scale, saved with the connection — is not read here at all.
///
/// LEC's three providers map onto GDS Viewer's four. Anthropic is Claude. Gemini is Google's
/// models, which the assistant reaches through Google's OpenAI-compatible endpoint
/// (<see cref="LlmEndpoints.ChatUrl"/>), where reasoning_effort means what it means at OpenAI.
/// And an OpenAI-compatible connection follows OpenAI's rules only at OpenAI's own address:
/// anywhere else it is a server of one's own, which may be anything at all — and a chat model
/// refuses a whole request over a reasoning_effort it does not take.
///
/// Each decision reads <see cref="AiConnection.EffectiveModel"/>, the model a request actually
/// names. GDS Viewer read the model as typed, so a Claude model left blank ran its default model
/// and was offered no effort; here what is offered is what is sent.
/// </summary>
public static class AiModels
{
    private static readonly string[] None = [];
    private static readonly string[] ThreeLevels = ["low", "medium", "high"];
    private static readonly string[] ClaudeFourSix = ["low", "medium", "high", "max"];
    private static readonly string[] ClaudeAll = ["low", "medium", "high", "xhigh", "max"];

    /// <summary>
    /// The effort levels a model takes, lowest first — none where it takes no level at all.
    ///
    /// OpenAI's reasoning models read reasoning_effort, and its chat models treat it as an error
    /// rather than ignoring it. Google's compatible endpoint takes the same field on the models
    /// that think, which is 2.5 and up. Both go to high.
    ///
    /// Anthropic reads output_config.effort — from Opus 4.5, which took three levels; Opus and
    /// Sonnet 4.6 added max, and Opus 4.7 xhigh, which every Claude since takes. Sonnet 4.5 and
    /// Haiku 4.5 refuse the field rather than ignoring it, so they are offered none.
    /// </summary>
    public static IReadOnlyList<string> EffortLevels(AiConnection? connection)
    {
        if (connection is null) return None;

        string model = connection.EffectiveModel;

        return connection.Provider switch
        {
            AiProvider.Anthropic => ClaudeLevels(model),
            AiProvider.Gemini => IsThinkingGemini(model) ? ThreeLevels : None,
            _ => IsOpenAi(connection) && IsOpenAiReasoner(model) ? ThreeLevels : None,
        };
    }

    /// <summary>Whether an effort level means anything here — whether <see cref="EffortLevels"/> offers any.</summary>
    public static bool SupportsEffort(AiConnection? connection) => EffortLevels(connection).Count > 0;

    /// <summary>
    /// The effort a request goes with: <paramref name="effort"/> when the model takes that level,
    /// and none otherwise. A level chosen for another model is refused by this one rather than
    /// ignored, so it is dropped here rather than sent — in the provider as well as wherever it was
    /// chosen, so a stale choice is never what a request is refused over.
    /// </summary>
    public static string ResolveEffort(AiConnection connection, string? effort)
        => effort is not null && EffortLevels(connection).Contains(effort) ? effort : "";

    /// <summary>
    /// Whether a model runs in fast mode: the same model writing up to two and a half times as
    /// fast, at twice the price. Only Anthropic offers it, and only on Opus 4.8 and Opus 5 on —
    /// Opus 4.7's was taken away, and a request asking it of any other model is refused rather than
    /// run slowly. By version, so an Opus released later is offered it too. The provider's
    /// documents, as GDS Viewer read them on 2026-11-01.
    /// </summary>
    public static bool SupportsFast(AiConnection? connection)
    {
        if (connection is null || connection.Provider != AiProvider.Anthropic) return false;

        var (family, version) = ClaudeVersion(connection.EffectiveModel);
        return family == "opus" && (version >= 5 || version == 4.8);
    }

    /// <summary>
    /// What a model does when it is sent no effort level, as its provider documents it — for the
    /// first row of a list of levels, which said only "default" in GDS Viewer and so told nobody
    /// how hard the model would think.
    ///
    /// By family, read from the version number as <see cref="EffortLevels"/> is: OpenAI's o-series
    /// and GPT-5 reason at medium unless told otherwise, and GPT-5.1 and after at none; Gemini 3
    /// and after think at high, 2.5 Flash-Lite not at all, and the rest of 2.5 as much as a
    /// question needs; Opus 5.5 and Haiku 5.5 at medium, and every other Claude that takes a
    /// level at high. Empty where no effort is offered, which is also where no list is shown.
    ///
    /// **The providers' documents, not a measurement.** Nothing in an answer says what level it
    /// was given, so a family that changes its default is only noticed here by somebody reading
    /// the change.
    /// </summary>
    public static string DefaultEffort(AiConnection? connection)
    {
        if (connection is null || !SupportsEffort(connection)) return "";

        string model = connection.EffectiveModel;

        switch (connection.Provider)
        {
            case AiProvider.Anthropic:
                var (family, version) = ClaudeVersion(model);
                return family is "opus" or "haiku" && version >= 5.5 ? "medium" : "high";

            case AiProvider.Gemini:
                if (VersionAfter(model, "gemini-") >= 3) return "high";
                return model.Contains("flash-lite", StringComparison.OrdinalIgnoreCase) ? "none" : "as needed";

            default:
                return VersionAfter(model, "gpt-") > 5 ? "none" : "medium";
        }
    }

    /// <summary>
    /// Whether a connection is OpenAI's own: https, at api.openai.com. Read off the address's host
    /// rather than the start of the string, so an address that only begins with OpenAI's name is
    /// not mistaken for it — the mistake that matters here is the one that sends a field a server
    /// refuses, and the safe one is to send nothing.
    /// </summary>
    internal static bool IsOpenAi(AiConnection connection)
        => Uri.TryCreate(connection.EffectiveBaseUrl.Trim(), UriKind.Absolute, out Uri? address)
        && address.Scheme == Uri.UriSchemeHttps
        && address.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// OpenAI's reasoning models: the o-series, and GPT from 5 on. GDS Viewer named gpt-5 as a
    /// prefix, which left the GPT-6 models its own price table lists without a level to choose;
    /// read as a version number, they land with GPT-5 as the rest of this class means them to.
    /// </summary>
    private static bool IsOpenAiReasoner(string model)
        => model.StartsWith("o1", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
        || model.StartsWith("o4", StringComparison.OrdinalIgnoreCase)
        || VersionAfter(model, "gpt-") >= 5;

    /// <summary>
    /// Whether a Gemini model id names a thinking model, read as a version number — gemini-2.5-flash
    /// is 2.5 and gemini-3-pro is 3 — so a family released later is offered an effort level instead
    /// of silently missing one. Thinking arrived with 2.5, and everything after it thinks too.
    /// </summary>
    private static bool IsThinkingGemini(string model) => VersionAfter(model, "gemini-") >= 2.5;

    private static IReadOnlyList<string> ClaudeLevels(string model)
    {
        var (family, version) = ClaudeVersion(model);

        return family switch
        {
            "opus" when version >= 4.7 => ClaudeAll,
            "opus" when version >= 4.6 => ClaudeFourSix,
            "opus" when version >= 4.5 => ThreeLevels,
            "sonnet" when version >= 5 => ClaudeAll,
            "sonnet" when version >= 4.6 => ClaudeFourSix,
            "haiku" or "fable" or "mythos" when version >= 5 => ClaudeAll,
            _ => None,
        };
    }

    /// <summary>
    /// A Claude model id read as its family and version: claude-opus-5-5 is opus 5.5,
    /// claude-sonnet-5 sonnet 5, and claude-opus-4-5-20251101 opus 4.5 — a part longer than two
    /// digits is a date, not a minor version. An id in no such shape is no family and version -1.
    /// </summary>
    private static (string Family, double Version) ClaudeVersion(string model)
    {
        string[] parts = model.Trim().ToLowerInvariant().Split('-');

        if (parts.Length < 3 || parts[0] != "claude"
         || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            return ("", -1);

        double version = major;
        if (parts.Length > 3 && parts[3].Length <= 2
         && int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int minor))
            version = major + minor / 10.0;

        return (parts[1], version);
    }

    /// <summary>
    /// The version at the front of a model id, after its family's prefix — 2.5 for
    /// gemini-2.5-flash, 5 for gpt-5-mini, 5.1 for gpt-5.1-codex — or -1 for an id that does not
    /// start with the prefix and a number.
    /// </summary>
    private static double VersionAfter(string model, string prefix)
    {
        model = model.Trim();
        if (!model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return -1;

        string rest = model[prefix.Length..];
        int end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] == '.')) end++;

        return double.TryParse(rest[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out double version)
            ? version
            : -1;
    }
}
