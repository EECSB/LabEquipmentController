using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Bench;

/// <summary>Server-side AI configuration, read once from configuration.</summary>
/// <remarks>
/// The desktop app keeps the user's key encrypted under Windows DPAPI, which does not exist
/// in a Linux container and is per-user anyway. Here the key comes from configuration —
/// an environment variable, a Docker secret, or user-secrets in development — which means
/// it is <em>one key shared by everyone who can reach the page</em>. That is fine on a
/// private bench network and wrong on a public one, and the UI says so rather than leaving
/// it to be discovered.
///
/// The key is never sent to the browser. Only whether one is configured, and which model it
/// names, cross the wire.
/// </remarks>
public sealed class AiOptions
{
    public const string Section = "Ai";

    public string ApiKey { get; set; } = "";
    public string Provider { get; set; } = "Gemini";
    public string Model { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 300;

    public bool Configured => ApiKey.Length > 0;
}

public sealed class AiService
{
    private readonly AiOptions _options;
    private readonly BenchService _bench;
    private readonly IAiClient _client;
    private readonly ILogger<AiService> _log;

    public AiService(AiOptions options, BenchService bench, IAiClient client, ILogger<AiService> log)
        => (_options, _bench, _client, _log) = (options, bench, client, log);

    public AiStatus Status()
    {
        var c = Connection();
        return new AiStatus(
            _options.Configured, c.Provider.ToString(), c.EffectiveModel,
            _options.Configured
                ? null
                : "No API key is configured. Set Ai__ApiKey in the environment (see the compose file) and restart.");
    }

    private AiConnection Connection()
    {
        var c = new AiConnection { TimeoutSeconds = _options.TimeoutSeconds };
        if (Enum.TryParse<AiProvider>(_options.Provider, ignoreCase: true, out var p)) c.Provider = p;
        if (_options.Model.Length > 0) c.Model = _options.Model;
        if (_options.BaseUrl.Length > 0) c.BaseUrl = _options.BaseUrl;
        return c;
    }

    public async Task<AiScriptReply> WriteScriptAsync(AiScriptRequest req, CancellationToken ct)
    {
        if (!_options.Configured) return new AiScriptReply("", [], Status().Reason);

        var instruments = new List<ScriptContextInstrument>();
        foreach (string id in req.SessionIds)
        {
            var s = _bench.Raw(id);
            if (s is null) continue;
            // The alias is what a sequence addresses the instrument by; the desktop app
            // derives it from the model the same way.
            string alias = new string(s.Profile.Name.Where(char.IsLetterOrDigit).Take(8).ToArray()).ToLowerInvariant();
            instruments.Add(new ScriptContextInstrument(alias, s.Identity, s.Address, CommandReference.ForFamily(s.Family)));
        }
        if (instruments.Count == 0)
            return new AiScriptReply("", [], "Connect an instrument first — the model is only allowed the commands in its catalog.");

        try
        {
            var author = new ScriptAuthor(_client);
            var result = await author.WriteAsync(
                req.Request, instruments, req.IsSequence, Connection(), _options.ApiKey,
                req.CurrentScript, req.RecentOutput, ct);
            return new AiScriptReply(result.Script, result.Undocumented, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Script authoring failed");
            return new AiScriptReply("", [], ex.Message);
        }
    }

    public async Task<AiExtractReply> ExtractAsync(AiExtractRequest req, CancellationToken ct)
    {
        if (!_options.Configured) return new AiExtractReply(0, 0, [], null, Status().Reason);

        // The upload is written to a temp file because the extractor reads documents from
        // disk — PDF, DOCX or text — and unpicking that to take a stream would change Core
        // for the sake of this one caller.
        string ext = Path.GetExtension(req.FileName);
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        try
        {
            await File.WriteAllBytesAsync(temp, Convert.FromBase64String(req.Base64), ct);
            var extractor = new CommandExtractor(_client);
            var result = await extractor.ExtractAsync(Connection(), _options.ApiKey, temp, null, ct);

            string? saved = null;
            if (result.Commands.Count > 0 && req.InstrumentKey is { Length: > 0 })
            {
                // Kept apart from the curated catalogs, as on the desktop: extracted
                // commands live in their own store and are never mixed into the transcribed
                // ones. In a container this path is inside the image unless a volume is
                // mounted for it, which the compose file does.
                var reference = new CommandReference
                {
                    Instrument = req.InstrumentKey,
                    Source = $"Read from {Path.GetFileName(req.FileName)} by {Connection().EffectiveModel}, not transcribed by hand.",
                    Commands = result.Commands,
                };
                ExtractedCatalogStore.Save(req.InstrumentKey, reference);
                saved = ExtractedCatalogStore.PathFor(req.InstrumentKey);
            }

            return new AiExtractReply(
                result.Commands.Count, result.Rejected.Count,
                result.Commands.Select(Map).ToList(), saved, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Extraction failed");
            return new AiExtractReply(0, 0, [], null, ex.Message);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    internal static CatalogCommandDto Map(CommandRef c) =>
        new(c.Category, c.Syntax, c.Description, c.Example, c.IsQuery, c.BenchVerified, c.CrossChecked, c.AiExtracted);
}
