using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>Progress while a datasheet is being worked through, for a status line.</summary>
public sealed record ExtractionProgress(int Chunk, int OfChunks, int FoundSoFar, string Stage);

/// <summary>What came back, plus anything the model produced that had to be dropped.</summary>
public sealed record ExtractionResult(
    IReadOnlyList<CommandRef> Commands,
    IReadOnlyList<string> Rejected);

/// <summary>
/// Asks a model to read an instrument datasheet and list the SCPI commands in it.
///
/// SPEC section 10 says never invent SCPI, and a language model is exactly the machine that
/// will cheerfully do so. Three things keep that in check, none of which is the prompt:
///
/// <list type="bullet">
/// <item>every command comes back marked <see cref="CommandRef.AiExtracted"/>, and is stored
///       apart from the transcribed catalogs — see <see cref="ExtractedCatalogStore"/>;</item>
/// <item>anything that is not a syntactically valid SCPI header is dropped here rather than
///       shown, and reported in <see cref="ExtractionResult.Rejected"/>;</item>
/// <item>nothing is saved until a human has looked at the list and accepted it.</item>
/// </list>
///
/// The prompt asks for transcription and forbids invention, which helps and does not
/// guarantee. Treat the output as a draft of a catalog, never as one.
/// </summary>
public sealed class CommandExtractor
{
    private readonly IAiClient _client;

    /// <summary>
    /// How much text goes in one request. Well under any model's limit: the binding
    /// constraint is answer quality, which falls off long before the context does.
    /// </summary>
    public int ChunkChars { get; init; } = 60_000;

    public CommandExtractor(IAiClient client) => _client = client;

    private const string Instruction = """
        You are transcribing an instrument's programming guide into a list of SCPI commands.

        Rules:
        - Transcribe only commands that appear in this document. Do not add commands you know
          from other instruments, and do not complete a family you think is missing. A command
          you cannot see in the text is a command that does not exist.
        - Keep the vendor's own capitalisation, which encodes the short form: MEASure:VOLTage
          means MEAS and MEASure are both accepted. Never "tidy" it.
        - Keep placeholders and optional parts exactly as printed: <n>, {AC|DC}, [:STATe],
          [SOURce:].
        - description is one plain sentence from the document's own wording. Do not invent an
          explanation for a command whose description you cannot find; leave it empty.
        - category is the subsystem heading it sits under, e.g. "MEASure", "TRIGger",
          "IEEE 488.2 Common".
        - isQuery is true when the command ends in '?'.
        - example only if the document shows one.

        Return every command you find. If the document contains none, return an empty list.
        """;

    /// <summary>The shape the reply must take.</summary>
    public static JsonNode Schema() => JsonNode.Parse("""
        {
          "type": "object",
          "properties": {
            "commands": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "syntax":      { "type": "string" },
                  "description": { "type": "string" },
                  "category":    { "type": "string" },
                  "example":     { "type": "string" },
                  "isQuery":     { "type": "boolean" }
                },
                "required": ["syntax"]
              }
            }
          },
          "required": ["commands"]
        }
        """)!;

    /// <summary>
    /// Read <paramref name="path"/> and extract its commands. A PDF is uploaded whole where
    /// the connection allows it and the user has not asked otherwise; anything else — and
    /// anything going to an endpoint that cannot read a document — is turned into text here
    /// and sent in chunks.
    /// </summary>
    public async Task<ExtractionResult> ExtractAsync(
        AiConnection connection, string apiKey, string path,
        IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
    {
        DocumentKind kind = DocumentText.KindOf(path);
        JsonNode schema = Schema();

        var found = new List<CommandRef>();
        var rejected = new List<string>();

        if (!connection.ExtractLocallyFor(kind))
        {
            // Checked before the file is read, let alone encoded and sent: a guide that is
            // over the provider's cap should say so at once, in terms the user can act on,
            // rather than after a slow upload fails with the provider's own wording.
            var file = new FileInfo(path);
            string? refusal = AiUploadLimits.Check(
                connection.Info, file.Name, file.Length, DocumentText.PageCount(path));
            if (refusal != null) throw new AiException(refusal);

            // Straight up as a document: one request, layout intact.
            progress?.Report(new ExtractionProgress(1, 1, 0, "Uploading the document…"));
            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            string reply = await _client.CompleteAsync(
                connection, apiKey, Instruction,
                AiPayload.FromDocument(bytes, "application/pdf"), schema, ct).ConfigureAwait(false);
            Collect(reply, found, rejected);
        }
        else
        {
            progress?.Report(new ExtractionProgress(0, 0, 0, "Reading the document…"));
            string text = DocumentText.Read(path);
            IReadOnlyList<string> chunks = DocumentText.Chunk(text, ChunkChars);

            for (int i = 0; i < chunks.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new ExtractionProgress(
                    i + 1, chunks.Count, found.Count, $"Reading part {i + 1} of {chunks.Count}…"));

                string reply = await _client.CompleteAsync(
                    connection, apiKey, Instruction,
                    AiPayload.FromText(chunks[i]), schema, ct).ConfigureAwait(false);
                Collect(reply, found, rejected);
            }
        }

        return new ExtractionResult(Dedupe(found), rejected);
    }

    // ------------------------------------------------------------------------- parsing

    private static void Collect(string reply, List<CommandRef> into, List<string> rejected)
    {
        foreach (JsonNode? item in ArrayIn(reply))
        {
            string syntax = (item?["syntax"]?.GetValue<string>() ?? "").Trim();
            if (syntax.Length == 0) continue;

            // The one hard gate: it has to parse as a SCPI header. This is what stops prose,
            // page furniture and hallucinated sentences reaching the review list.
            if (!ScpiSyntax.IsValidTemplate(syntax))
            {
                rejected.Add(syntax);
                continue;
            }

            string description = (item?["description"]?.GetValue<string>() ?? "").Trim();
            string category = (item?["category"]?.GetValue<string>() ?? "").Trim();
            string example = (item?["example"]?.GetValue<string>() ?? "").Trim();

            bool isQuery = item?["isQuery"]?.GetValueKind() == JsonValueKind.True
                        || syntax.TrimEnd().EndsWith('?');

            into.Add(new CommandRef(
                Category: category.Length > 0 ? category : "Extracted",
                Syntax: syntax,
                Description: description,
                Example: example.Length > 0 ? example : null,
                IsQuery: isQuery,
                BenchVerified: false,
                CrossChecked: false,
                AiExtracted: true));
        }
    }

    /// <summary>
    /// The commands array out of a reply. Models wrap JSON in prose or fences often enough
    /// that the braces are found rather than assumed.
    /// </summary>
    private static JsonArray ArrayIn(string reply)
    {
        foreach (string candidate in Candidates(reply))
        {
            try
            {
                JsonNode? root = JsonNode.Parse(candidate);
                if (root?["commands"] is JsonArray a) return a;
                if (root is JsonArray bare) return bare;
            }
            catch { /* try the next candidate */ }
        }
        return new JsonArray();
    }

    private static IEnumerable<string> Candidates(string reply)
    {
        yield return reply;

        int open = reply.IndexOf('{');
        int close = reply.LastIndexOf('}');
        if (open >= 0 && close > open) yield return reply[open..(close + 1)];

        int openA = reply.IndexOf('[');
        int closeA = reply.LastIndexOf(']');
        if (openA >= 0 && closeA > openA) yield return reply[openA..(closeA + 1)];
    }

    /// <summary>
    /// One entry per command. Chunks overlap in practice — a subsystem's heading repeats on
    /// every page — and the same command coming back three times is noise in the review list.
    /// </summary>
    private static List<CommandRef> Dedupe(IEnumerable<CommandRef> commands)
    {
        var seen = new Dictionary<string, CommandRef>(StringComparer.OrdinalIgnoreCase);
        foreach (CommandRef c in commands)
        {
            string key = c.Syntax.Trim();
            // Keep whichever copy actually carries a description.
            if (!seen.TryGetValue(key, out CommandRef? kept)
                || (kept.Description.Length == 0 && c.Description.Length > 0))
            {
                seen[key] = c;
            }
        }
        return seen.Values.OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(c => c.Syntax, StringComparer.OrdinalIgnoreCase)
                          .ToList();
    }
}
