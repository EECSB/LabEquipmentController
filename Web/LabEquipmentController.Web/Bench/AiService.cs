using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Bench;

/// <summary>The AI connection as configuration supplies it, before anyone edits it.</summary>
/// <remarks>
/// This is the bottom layer only: an environment variable, a Docker secret, or user-secrets
/// in development, which is how a container starts with a working connection and nobody at a
/// keyboard. <see cref="AiSettingsStore"/> lays whatever was last applied in the settings box
/// on top of it, and is what everything else reads.
///
/// Either way it is <em>one key shared by everyone who can reach the page</em>, because there
/// are no accounts here for people to have a key each. That is fine on a private bench
/// network and wrong on a public one, and the UI says so rather than leaving it to be found
/// out.
///
/// The key is never sent to the browser. Only whether there is one, and where it came from.
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
    private readonly AiSettingsStore _settings;
    private readonly BenchService _bench;
    private readonly IAiClient _client;
    private readonly ILogger<AiService> _log;

    public AiService(AiSettingsStore settings, BenchService bench, IAiClient client, ILogger<AiService> log)
        => (_settings, _bench, _client, _log) = (settings, bench, client, log);

    public AiStatus Status()
    {
        var c = Connection();
        // The list to choose from, with the labels that keep two connections on one model
        // apart — composed in Core, so the desktop's picker and this one read the same.
        AiConnections book = _settings.Book;
        IReadOnlyList<string> labels = book.Labels();
        var connections = new List<AiConnectionDto>(book.Items.Count);
        for (int i = 0; i < book.Items.Count; i++)
        {
            AiConnection one = book.Items[i];
            connections.Add(new AiConnectionDto(
                one.Id, labels[i], one.Name, one.Info.Label, one.EffectiveModel,
                _settings.ConfiguredFor(one.Id)));
        }

        return new AiStatus(
            _settings.Configured, c.Provider.ToString(), c.EffectiveModel, c.EffectiveBaseUrl, c.TimeoutSeconds,
            c.ExtractTextLocally, _settings.KeyFromConfiguration,
            _settings.Configured
                ? null
                // Read in two places — the settings box, which has the field right there, and
                // a console's AI tools, which do not — so it says what is true rather than
                // pointing anywhere. Each surface adds its own "and here is where".
                : book.Items.Count == 0
                    ? "No AI connection is set up, so the AI features are off."
                    : "No API key is set on this connection, so the AI features are off.",
            Providers, c.Effort.ToString(), Efforts, connections, book.SelectedId);
    }

    /// <summary>
    /// Apply an edited connection and answer with what it now is, so the box that sent it is
    /// showing the server's state rather than its own idea of what it asked for.
    /// </summary>
    public AiSettingsReply Apply(AiSettingsUpdate update)
    {
        string? error = _settings.Apply(update);
        return error is null ? new AiSettingsReply(Status(), null) : new AiSettingsReply(null, error);
    }

    /// <summary>Another connection on a provider's defaults, selected, and the state after.</summary>
    public AiSettingsReply AddConnection(string? provider)
    {
        _settings.Add(provider);
        return new AiSettingsReply(Status(), null);
    }

    /// <summary>Forget one, and its key with it.</summary>
    public AiSettingsReply RemoveConnection(string id)
        => _settings.Remove(id)
            ? new AiSettingsReply(Status(), null)
            : new AiSettingsReply(null, "There is no such connection.");

    /// <summary>
    /// Use this one, everywhere.
    ///
    /// One selection rather than one per window: picking a connection in the datasheet window
    /// is picking the connection, the same rule the PDF switch and the effort setting are held
    /// to. Two places that can disagree about what is in force is one place too many.
    /// </summary>
    public AiSettingsReply SelectConnection(string id)
        => _settings.Select(id)
            ? new AiSettingsReply(Status(), null)
            : new AiSettingsReply(null, "There is no such connection.");

    /// <summary>
    /// The presets, as Core holds them. The browser cannot reach Core — the client half has
    /// no instrument logic in it — so the provider list travels with the status.
    /// </summary>
    /// <summary>
    /// The effort scale, as Core spells it. Travels with the status for the same reason the
    /// provider list does: the browser cannot reach Core, so a settings box would otherwise
    /// be hard-coding a copy of an enum it cannot see.
    /// </summary>
    private static IReadOnlyList<string> Efforts { get; } =
        Enum.GetNames<AiEffort>();

    private static IReadOnlyList<AiProviderDto> Providers { get; } =
        AiProviderInfo.Known.Select(p => new AiProviderDto(
            p.Provider.ToString(), p.Label, p.DefaultBaseUrl, p.DefaultModel,
            p.SupportsPdfUpload, p.PdfCostNote)).ToList();

    private AiConnection Connection() => _settings.Connection;

    public async Task<AiScriptReply> WriteScriptAsync(AiScriptRequest req, CancellationToken ct)
    {
        if (!_settings.Configured) return new AiScriptReply("", [], Status().Reason);

        IReadOnlyList<ScriptContextInstrument> instruments = Describe(req);
        if (instruments.Count == 0)
            return new AiScriptReply("", [], "Connect an instrument first — the model is only allowed the commands in its catalog.");

        try
        {
            var author = new ScriptAuthor(_client);
            var result = await author.WriteAsync(
                req.Request, instruments, req.IsSequence, Connection(), _settings.ApiKey,
                req.CurrentScript, req.RecentOutput, Conversation(req.History), ct);
            return new AiScriptReply(result.Script, result.Undocumented, null, result.Notes);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Script authoring failed");
            return new AiScriptReply("", [], ex.Message);
        }
    }

    /// <summary>
    /// The instruments a request may use, described as the desktop describes them — by the same
    /// Core code (<see cref="ScriptContext"/>), so both builds tell the model the same things.
    /// </summary>
    /// <remarks>
    /// This used to be done here, and differently: the alias was the first eight letters of the
    /// profile name, so two meters were both "multimet"; the identity went where the model goes
    /// and the address where the identity goes; and a single-instrument script was given an
    /// alias its lines must not carry.
    ///
    /// For a sequence the whole bench goes in and the ticks say what is described, because a
    /// meter left unticked is still a second meter of that model, and naming the ticked one by
    /// model would bind neither. In address order, so which of two meters is dmm and which is
    /// dmm2 does not depend on the order a dictionary hands them back in.
    ///
    /// The names come from the editor whether or not its script is sent to be revised, as
    /// SequenceForm reads them from its own; they were kept here only when it was, so an
    /// unticked "Revise" renamed every device. And the table's picks are taken as given, as the
    /// table itself is filled (<see cref="BenchService.BindSequence"/>): the part the writer is
    /// told a meter plays is the part the table says it plays.
    /// </remarks>
    private IReadOnlyList<ScriptContextInstrument> Describe(AiScriptRequest req)
    {
        var chosen = req.SessionIds.Select(_bench.Raw).OfType<BenchService.Session>().ToList();
        if (!req.IsSequence)
            return chosen.Select(s => ScriptContext.ForScript(s.Identity, s.Address)).ToList();

        var picked = new Dictionary<string, BenchService.Session>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, id) in req.Picks ?? new Dictionary<string, string>())
            if (_bench.Raw(id) is { } s) picked[alias] = s;

        var bench = _bench.Raw().OrderBy(s => s.Address, StringComparer.OrdinalIgnoreCase).ToList();
        return ScriptContext.ForSequence(bench, s => s.Identity, s => s.Host,
                                         req.EditorScript ?? req.CurrentScript, chosen, picked);
    }

    /// <summary>
    /// The browser's transcript, as Core's turns. Nothing is dropped or trimmed here: what
    /// the window says it is sending is what gets sent, because the figure beside its Clear
    /// button is the user's only way to decide whether to press it.
    /// </summary>
    private static IReadOnlyList<ScriptTurn> Conversation(IReadOnlyList<AiTurn>? history)
        => history is not { Count: > 0 }
            ? []
            : history.Select(t => new ScriptTurn(
                t.Request, t.Script, t.Notes, t.Undocumented)).ToList();

    public async Task<AiExtractReply> ExtractAsync(AiExtractRequest req, CancellationToken ct)
    {
        if (!_settings.Configured) return new AiExtractReply(0, 0, [], null, Status().Reason);

        // The upload is written to a temp file because the extractor reads documents from
        // disk — PDF, DOCX or text — and unpicking that to take a stream would change Core
        // for the sake of this one caller.
        string ext = Path.GetExtension(req.FileName);
        string temp = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ext);
        try
        {
            await File.WriteAllBytesAsync(temp, Convert.FromBase64String(req.Base64), ct);
            var extractor = new CommandExtractor(_client);
            var result = await extractor.ExtractAsync(Connection(), _settings.ApiKey, temp, null, ct);

            // Nothing is written here. What a model reads out of a datasheet is extracted,
            // not verified, and the desktop makes you look at the list and untick what is
            // wrong before Save Ticked writes any of it. Held under a token instead, so the
            // save can name the ones to keep without shipping them back up.
            string token = Guid.NewGuid().ToString("N");
            _extractions[token] = new Extraction(result.Commands, Path.GetFileName(req.FileName));
            Trim();

            return new AiExtractReply(
                result.Commands.Count, result.Rejected.Count,
                result.Commands.Select(Map).ToList(), token, null);
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

    /// <summary>
    /// Write the ticked commands out for this instrument.
    ///
    /// Kept apart from the curated catalogs, as on the desktop: extracted commands live in
    /// their own store and are never mixed into the transcribed ones. In a container this
    /// path is inside the image unless a volume is mounted for it, which the compose file
    /// does.
    /// </summary>
    public AiSaveExtractReply SaveExtracted(AiSaveExtractRequest req)
    {
        if (!_extractions.TryGetValue(req.Token, out Extraction? extraction))
            return new AiSaveExtractReply(0, null,
                "That extraction is no longer held — the server has restarted, or it has been "
                + "pushed out by later ones. Read the datasheet again.");

        // Keyed on the model rather than the address: an instrument on DHCP moves, and its
        // extracted commands should follow the instrument, not the lease. InstrumentConsole keys
        // its own the same way, out of the same parse, so a bench driven from both builds files
        // one instrument in one place.
        var session = _bench.Raw(req.SessionId);
        if (session is null)
            return new AiSaveExtractReply(0, null,
                "That instrument is not connected any more, so there is nothing to file these under.");

        (_, string model) = InstrumentProfile.ParseIdentity(session.Identity);
        string key = model.Length > 0 ? model : session.Host;
        string title = model.Length > 0 ? $"{model} ({session.Host})" : $"Instrument ({session.Host})";

        var keep = req.Keep
            .Where(i => i >= 0 && i < extraction.Commands.Count)
            .Select(i => extraction.Commands[i])
            .ToList();

        if (keep.Count == 0)
            return new AiSaveExtractReply(0, null, "Nothing is ticked, so there is nothing to save.");

        var reference = new CommandReference
        {
            Instrument = title,
            // Word for word the desktop's, because it is the sentence a reader meets in the
            // catalog months later and has to weigh what is in it by.
            Source = $"Extracted from {extraction.FileName} by {Connection().Info.Label} "
                   + $"({Connection().EffectiveModel}). "
                   + "Not verified against the instrument or a vendor guide.",
            Commands = keep,
        };
        ExtractedCatalogStore.Save(key, reference, _settings.ExtractedDirectory);

        return new AiSaveExtractReply(
            keep.Count, ExtractedCatalogStore.PathFor(key, _settings.ExtractedDirectory), null);
    }

    /// <summary>What one extraction produced, waiting to be ticked over.</summary>
    private sealed record Extraction(IReadOnlyList<CommandRef> Commands, string FileName);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Extraction> _extractions = new();

    /// <summary>
    /// Hold the last few and let the rest go. A guide of a thousand commands is a few hundred
    /// kilobytes, and these are only alive between reading a datasheet and deciding what to
    /// keep of it — minutes, on one bench, by one person.
    /// </summary>
    private void Trim()
    {
        const int keep = 4;
        while (_extractions.Count > keep)
            foreach (string old in _extractions.Keys.Take(_extractions.Count - keep))
                _extractions.TryRemove(old, out _);
    }

    internal static CatalogCommandDto Map(CommandRef c) =>
        new(c.Category, c.Syntax, c.Description, c.Example, c.IsQuery, c.BenchVerified, c.CrossChecked, c.AiExtracted);
}
