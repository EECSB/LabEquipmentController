using System;

namespace LabEquipmentController;

/// <summary>
/// Checks a document against what a provider will accept before it is sent.
///
/// Without this the failure lands as a raw API error after the whole file has been read,
/// base64-encoded and pushed over the wire — slow, and phrased in the provider's terms
/// rather than in terms of anything the user can do. A programming guide is exactly the kind
/// of document that trips these limits: vendor guides run to several hundred pages and tens
/// of megabytes.
///
/// Pure and separate from the extractor so the arithmetic can be tested without conjuring a
/// 32 MB fixture.
/// </summary>
public static class AiUploadLimits
{
    /// <summary>
    /// Null when the document can be uploaded, or a sentence explaining what is wrong and
    /// what to do about it. <paramref name="pages"/> may be 0 when the count is unknown, in
    /// which case only the size is judged.
    /// </summary>
    public static string? Check(AiProviderInfo info, string fileName, long rawBytes, int pages)
    {
        if (info == null) throw new ArgumentNullException(nameof(info));

        if (!info.SupportsPdfUpload)
            return $"{info.Label} cannot accept an uploaded document.";

        if (info.MaxUploadBytes > 0)
        {
            long counted = info.CountedSizeOf(rawBytes);
            if (counted > info.MaxUploadBytes)
            {
                string detail = info.LimitIsOnRequestPayload
                    ? $"{Mb(rawBytes)} becomes {Mb(counted)} once encoded for the request, "
                    + $"and {info.Label} accepts {Mb(info.MaxUploadBytes)} per request"
                    : $"it is {Mb(rawBytes)}, and {info.Label} accepts {Mb(info.MaxUploadBytes)}";

                return $"“{fileName}” is too large to upload: {detail}. {Remedy}";
            }
        }

        if (info.MaxPdfPages > 0 && pages > info.MaxPdfPages)
            return $"“{fileName}” has {pages:N0} pages, and {info.Label} accepts "
                 + $"{info.MaxPdfPages:N0} per request. {Remedy}";

        return null;
    }

    private const string Remedy =
        "Tick “Extract text locally before sending”, or split the document into sections "
      + "and extract them one at a time.";

    /// <summary>Sizes in the units the provider's own documentation quotes.</summary>
    private static string Mb(long bytes) => $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
