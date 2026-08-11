using System;
using System.Collections.Generic;

namespace LabEquipmentController;

/// <summary>Which request shape a connection speaks.</summary>
public enum AiProvider
{
    /// <summary>Google Gemini, Interactions API (POST /v1beta/interactions).</summary>
    Gemini,

    /// <summary>Anthropic Messages API (POST /v1/messages).</summary>
    Anthropic,

    /// <summary>
    /// Anything speaking OpenAI's POST /chat/completions — OpenAI itself, OpenRouter, Groq,
    /// Ollama, LM Studio, vLLM.
    /// </summary>
    OpenAiCompatible,
}

/// <summary>
/// What a provider can do, and the defaults its preset fills in.
///
/// The only capability the rest of the app branches on is
/// <see cref="SupportsPdfUpload"/>: where it is false a PDF has to be flattened to text
/// locally before it can be sent at all, so the "extract text locally" choice stops being a
/// choice.
/// </summary>
public sealed record AiProviderInfo(
    AiProvider Provider,
    string Label,
    string DefaultBaseUrl,
    string DefaultModel,
    bool SupportsPdfUpload,
    string PdfCostNote,
    long MaxUploadBytes,
    bool LimitIsOnRequestPayload,
    int MaxPdfPages)
{
    /// <summary>
    /// Size this upload counts as against <see cref="MaxUploadBytes"/>.
    ///
    /// The distinction matters. Anthropic caps the whole request payload, and a PDF travels
    /// base64-encoded — four bytes out for every three in — so a 24 MB file is already over
    /// a 32 MB request cap before the prompt is added. Gemini caps the file itself, where
    /// the raw size is the one to compare.
    /// </summary>
    public long CountedSizeOf(long rawBytes)
        => LimitIsOnRequestPayload ? 4 * ((rawBytes + 2) / 3) : rawBytes;

    private static readonly AiProviderInfo[] All =
    {
        new(AiProvider.Gemini, "Google Gemini",
            "https://generativelanguage.googleapis.com", "gemini-3.6-flash",
            SupportsPdfUpload: true,
            "Gemini bills a PDF at 258 tokens per page and does not charge for text already "
          + "in the file, so uploading it is usually several times cheaper than sending the "
          + "same pages as extracted text.",
            MaxUploadBytes: 50L * 1024 * 1024,   // the file itself, per Gemini's limits
            LimitIsOnRequestPayload: false,
            MaxPdfPages: 1000),

        new(AiProvider.Anthropic, "Anthropic Claude",
            "https://api.anthropic.com", "claude-sonnet-5",
            SupportsPdfUpload: true,
            "Claude renders each page to an image as well as reading its text and bills for "
          + "both, so uploading costs more per page than sending extracted text — but keeps "
          + "the layout.",
            // The cap is on the whole request payload, so the base64 expansion counts.
            MaxUploadBytes: 32L * 1024 * 1024,
            LimitIsOnRequestPayload: true,
            MaxPdfPages: 600),

        new(AiProvider.OpenAiCompatible, "OpenAI-compatible endpoint",
            "https://api.openai.com", "gpt-4o-mini",
            SupportsPdfUpload: false,
            "Covers OpenAI, OpenRouter, Groq, Ollama and LM Studio. Most such endpoints "
          + "cannot accept a file at all, so a PDF has to be converted to text first.",
            // No upload path, so no upload limits to police.
            MaxUploadBytes: 0,
            LimitIsOnRequestPayload: false,
            MaxPdfPages: 0),
    };

    /// <summary>Every known provider, in the order they should be offered.</summary>
    public static IReadOnlyList<AiProviderInfo> Known => All;

    public static AiProviderInfo For(AiProvider provider)
        => Array.Find(All, p => p.Provider == provider) ?? All[0];
}

/// <summary>
/// A user's AI connection. Persisted as part of <see cref="UserSettings"/> — except the key,
/// which is held separately and encrypted (see the app's <c>SecretStore</c>). Core stays
/// portable and never touches Windows crypto; it only ever sees the key as a string that
/// someone else decrypted.
/// </summary>
public sealed class AiConnection
{
    public AiProvider Provider { get; set; } = AiProvider.Gemini;

    /// <summary>Scheme and host, no trailing slash. Empty means "use the provider default".</summary>
    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>
    /// Flatten PDFs to text here before sending, rather than uploading the file.
    ///
    /// Three-state on purpose. <c>null</c> means "whatever suits this provider", which is
    /// what a connection starts as and returns to when the provider changes. Once the user
    /// ticks or unticks the box it becomes explicit and is left alone.
    /// See <see cref="EffectiveExtractTextLocally"/>.
    /// </summary>
    public bool? ExtractTextLocally { get; set; }

    /// <summary>
    /// Whether local extraction is on, resolving <c>null</c> to the default: off wherever the
    /// provider can take the file itself, and on where it cannot because there is no
    /// alternative. Uploading is both cheaper on most providers and better, since flattening
    /// a two-column guide interleaves its columns.
    /// </summary>
    public bool EffectiveExtractTextLocally
        => ExtractTextLocally ?? !Info.SupportsPdfUpload;

    /// <summary>True when the setting is following the provider rather than an explicit choice.</summary>
    public bool FollowsProviderDefault => ExtractTextLocally == null;

    /// <summary>
    /// Hover text for the checkbox: what it does, why it exists at all, and what it costs
    /// either way on this particular provider.
    /// </summary>
    public string LocalExtractionHelp
    {
        get
        {
            string what =
                "Convert the PDF to plain text on this PC and send the text, instead of "
              + "uploading the file itself.\r\n\r\n";

            string why = Info.SupportsPdfUpload
                ? "Off by default: this provider reads the PDF directly, which keeps the page "
                + "layout. Flattening a two-column programming guide interleaves its columns, "
                + "which is how a command ends up with its neighbour's description.\r\n\r\n"
                : "Forced on: this provider cannot accept a file, so there is no other way to "
                + "send a PDF to it.\r\n\r\n";

            return what + why + "Cost: " + Info.PdfCostNote;
        }
    }

    /// <summary>
    /// Seconds to allow one request. Datasheet chunks are large and models are slow.
    ///
    /// 300 rather than 120, because a whole programming guide in one request is the ordinary
    /// case rather than the extreme one: where a provider takes a PDF directly the document
    /// is not chunked at all, so a 158-page guide is a single call producing two hundred
    /// structured results. At 120 that timed out against Gemini every time, and the failure
    /// arrives after two minutes of waiting and costs the call.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 300;

    public AiProviderInfo Info => AiProviderInfo.For(Provider);

    public string EffectiveBaseUrl =>
        string.IsNullOrWhiteSpace(BaseUrl) ? Info.DefaultBaseUrl : BaseUrl.TrimEnd('/');

    public string EffectiveModel =>
        string.IsNullOrWhiteSpace(Model) ? Info.DefaultModel : Model.Trim();

    /// <summary>
    /// Whether local extraction applies, given the provider. True whenever the provider
    /// cannot accept an uploaded PDF, whatever the stored preference says.
    /// </summary>
    public bool ExtractLocallyFor(DocumentKind kind)
        => kind != DocumentKind.Pdf || EffectiveExtractTextLocally || !Info.SupportsPdfUpload;

    /// <summary>Whether the user is allowed to turn local extraction off at all.</summary>
    public bool CanSendPdfDirectly => Info.SupportsPdfUpload;

    public AiConnection Clone() => new()
    {
        Provider = Provider,
        BaseUrl = BaseUrl,
        Model = Model,
        ExtractTextLocally = ExtractTextLocally,
        TimeoutSeconds = TimeoutSeconds,
    };
}
