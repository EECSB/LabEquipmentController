using System.Text.Json;
using LabEquipmentController.Web.Client.Contracts;

namespace LabEquipmentController.Web.Bench;

/// <summary>
/// The AI connections the server knows about, which one it is using, and the one place they
/// are changed.
///
/// Two layers. Underneath is configuration — <c>Ai__Provider</c>, <c>Ai__ApiKey</c> and the
/// rest, from the environment or the compose file — which is how a container is handed a
/// connection at start-up with nobody at a keyboard. On top is whatever was last applied from
/// the settings box, kept in <c>ai.json</c> beside the extracted catalogs so it survives the
/// container being replaced. The top layer wins where it has an answer, because it is the
/// more recent statement of intent.
///
/// It became a list when the model became a choice: reading a command out of a programming
/// guide and working out what a sequence of SCPI ought to be are different jobs, and the cheap
/// fast model that suits the first is not the one you want for the second. Configuration still
/// describes one connection — an operator handing a container a key is handing it one key —
/// and that one is the first entry of the list.
///
/// The desktop app keeps the same connections in <c>UserSettings</c> with the keys encrypted by
/// <c>SecretStore</c> under Windows DPAPI. That does not exist here: DPAPI is per Windows
/// account and this server has no accounts at all. So the keys are written to a file whose
/// permissions are narrowed to the server's own user where the platform has permissions to
/// narrow, and the settings box says as much rather than implying a safety it does not have.
/// </summary>
public sealed class AiSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AiOptions _configured;
    private readonly ILogger<AiSettingsStore> _log;
    private readonly Lock _gate = new();

    private State _state;

    /// <summary>
    /// An immutable snapshot. Swapped whole rather than edited in place, so a request that is
    /// reading a connection cannot see half of an edit.
    /// </summary>
    /// <param name="Book">The connections and which is selected.</param>
    /// <param name="Keys">One key per connection id. Absent means no key for that one.</param>
    /// <param name="FromConfiguration">
    /// Which ids hold a key that came from the environment. Those are never written back out:
    /// the key already lives somewhere the operator controls, and copying it into a file would
    /// create a second place to change it and a second place to leak it from.
    /// </param>
    private sealed record State(
        AiConnections Book,
        IReadOnlyDictionary<string, string> Keys,
        IReadOnlySet<string> FromConfiguration);

    public AiSettingsStore(AiOptions configured, IConfiguration config, ILogger<AiSettingsStore> log)
    {
        _configured = configured;
        _log = log;
        DataDirectory = Resolve(config);
        _state = Seed();
    }

    // ------------------------------------------------------------------ where things live

    /// <summary>
    /// Where this server keeps what it is given, as opposed to what it ships with.
    ///
    /// <c>LEC_DATA</c> is set by the Dockerfile and mounted as a volume by the compose file
    /// precisely so this survives the container being replaced. Off a container there is no
    /// such variable and it falls back to the per-user folder the desktop app writes to.
    ///
    /// Taken from configuration rather than straight off the environment so that a test can
    /// point it at a temporary folder. Reading it directly would mean the AI tests passed or
    /// failed according to whether the machine running them happens to have a key saved.
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>Catalogs a model read out of a datasheet, under the same roof.</summary>
    public string ExtractedDirectory => Path.Combine(DataDirectory, "extracted");

    private string SettingsPath => Path.Combine(DataDirectory, "ai.json");

    private static string Resolve(IConfiguration config)
        => config["LEC_DATA"] is { Length: > 0 } dir
            ? dir
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "LabEquipmentController");

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Every connection and which is selected. A copy: these are mutable, and a caller that
    /// adjusted the shared ones would change them for every other request.
    /// </summary>
    public AiConnections Book => _state.Book.Clone();

    /// <summary>The connection in use, or a bare default when there are none.</summary>
    public AiConnection Connection => (_state.Book.Selected ?? new AiConnection()).Clone();

    /// <summary>The key for the connection in use, or empty.</summary>
    public string ApiKey => KeyFor(_state.Book.Selected?.Id);

    /// <summary>Whether the connection in use has a key to spend.</summary>
    public bool Configured => ApiKey.Length > 0;

    /// <summary>True when the key in use came from the environment rather than this box.</summary>
    public bool KeyFromConfiguration
    {
        get
        {
            State now = _state;
            string? id = now.Book.Selected?.Id;
            return id is not null && now.FromConfiguration.Contains(id);
        }
    }

    /// <summary>The key stored against one connection, or empty.</summary>
    public string KeyFor(string? id)
    {
        State now = _state;
        return id is not null && now.Keys.TryGetValue(id, out string? key) ? key : "";
    }

    /// <summary>Whether one connection has a key at all — what a picker greys an entry by.</summary>
    public bool ConfiguredFor(string? id) => KeyFor(id).Length > 0;

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Apply an edited connection, or say why not. Returns null on success.
    ///
    /// Which connection is named by <see cref="AiSettingsUpdate.Id"/>; without one it is the
    /// selected connection, which is what a box editing "the connection in use" means. Editing
    /// one that is not there adds it — a browser that had the list open while another deleted
    /// an entry should not be told its Apply is invalid.
    ///
    /// The endpoint is checked the way the desktop's Apply button checks it — an absolute
    /// http or https address — because everything past this point assumes it can be handed
    /// to an HttpClient, and "api.openai.com" without a scheme cannot.
    /// </summary>
    public string? Apply(AiSettingsUpdate update)
    {
        if (!Enum.TryParse(update.Provider, ignoreCase: true, out AiProvider provider))
            return $"'{update.Provider}' is not a provider this build knows.";

        lock (_gate)
        {
            AiConnections book = _state.Book.Clone();
            string id = update.Id is { Length: > 0 } named ? named : book.Selected?.Id ?? "";

            AiConnection target = book.Find(id) ?? book.Add(provider);
            target.Provider = provider;
            target.Name = (update.Name ?? target.Name).Trim();
            target.BaseUrl = (update.Endpoint ?? "").Trim();
            target.Model = (update.Model ?? "").Trim();
            target.ExtractTextLocally = update.ExtractTextLocally;
            target.TimeoutSeconds = Math.Clamp(update.TimeoutSeconds, 15, 900);

            // Absent leaves the stored effort alone — the datasheet window applies the one
            // control beside it and says nothing about the rest — and a name this build does
            // not know falls back to asking for nothing rather than refusing an otherwise
            // good edit over a word.
            if (update.Effort is { Length: > 0 } asked)
                target.Effort = Enum.TryParse(asked, ignoreCase: true, out AiEffort chosen) ? chosen : AiEffort.Default;

            if (!Uri.TryCreate(target.EffectiveBaseUrl, UriKind.Absolute, out Uri? uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return "The endpoint needs to be a full http or https address, for example "
                     + "https://api.openai.com.";
            }

            // Three states, as the box offers: null leaves the stored key alone, empty
            // forgets it, anything else replaces it.
            var keys = new Dictionary<string, string>(_state.Keys);
            var fromConfig = new HashSet<string>(_state.FromConfiguration);
            switch (update.ApiKey)
            {
                case null: break;
                case "": keys.Remove(target.Id); fromConfig.Remove(target.Id); break;
                default: keys[target.Id] = update.ApiKey.Trim(); fromConfig.Remove(target.Id); break;
            }

            Commit(book, keys, fromConfig);

            // Worth a line in the log: it changes where this server sends a key, and the log is
            // the only record of that a bench server keeps.
            _log.LogInformation("AI connection {Name} set to {Provider} at {Endpoint} ({Model}), key {Key}.",
                target.EffectiveName, target.Provider, target.EffectiveBaseUrl, target.EffectiveModel,
                keys.ContainsKey(target.Id) ? "present" : "none");
        }

        return null;
    }

    /// <summary>
    /// Another connection, on a provider's defaults, selected. Hands back its id.
    ///
    /// Selected because adding one is how you say you want to use it; an addition that left the
    /// old one in force would be a button that appears to do nothing.
    /// </summary>
    public string Add(string? provider)
    {
        lock (_gate)
        {
            AiConnections book = _state.Book.Clone();
            AiConnection made = book.Add(
                Enum.TryParse(provider, ignoreCase: true, out AiProvider p) ? p : AiProvider.Gemini);

            Commit(book, _state.Keys, _state.FromConfiguration);
            return made.Id;
        }
    }

    /// <summary>
    /// Forget a connection and its key. The last one can go: a server with no AI connection on
    /// it is the state this starts in, so it has to be reachable again.
    /// </summary>
    public bool Remove(string id)
    {
        lock (_gate)
        {
            AiConnections book = _state.Book.Clone();
            if (!book.Remove(id)) return false;

            var keys = new Dictionary<string, string>(_state.Keys);
            keys.Remove(id);
            var fromConfig = new HashSet<string>(_state.FromConfiguration);
            fromConfig.Remove(id);

            Commit(book, keys, fromConfig);
            _log.LogInformation("AI connection {Id} removed.", id);
            return true;
        }
    }

    /// <summary>Use this one. False if it is not one of these.</summary>
    public bool Select(string id)
    {
        lock (_gate)
        {
            AiConnections book = _state.Book.Clone();
            if (!book.Select(id)) return false;

            Commit(book, _state.Keys, _state.FromConfiguration);
            return true;
        }
    }

    /// <summary>Swap the snapshot and write it out. Called under the lock, always.</summary>
    private void Commit(AiConnections book,
                        IReadOnlyDictionary<string, string> keys,
                        IReadOnlySet<string> fromConfiguration)
    {
        _state = new State(book, keys, fromConfiguration);
        Save(_state);
    }

    // ------------------------------------------------------------------ the two layers

    /// <summary>Configuration first, then whatever was applied here on top of it.</summary>
    private State Seed()
    {
        // The configured connection, which is one connection: an operator handing a container
        // a key is handing it one key.
        var configured = new AiConnection { TimeoutSeconds = _configured.TimeoutSeconds };
        if (Enum.TryParse(_configured.Provider, ignoreCase: true, out AiProvider p)) configured.Provider = p;
        if (_configured.Model.Length > 0) configured.Model = _configured.Model;
        if (_configured.BaseUrl.Length > 0) configured.BaseUrl = _configured.BaseUrl;

        AiConnections book = AiConnections.From(configured);
        var keys = new Dictionary<string, string>();
        var fromConfig = new HashSet<string>();
        if (_configured.ApiKey.Length > 0)
        {
            keys[configured.Id] = _configured.ApiKey;
            fromConfig.Add(configured.Id);
        }

        Saved? saved = Read();
        if (saved is null) return new State(book, keys, fromConfig);

        // A file written before there were several holds one connection at the top level. Read
        // as the first entry of a list, with its key, so that this change does not cost anyone
        // the connection they already had.
        List<SavedConnection> stored = saved.Connections is { Count: > 0 }
            ? saved.Connections
            : [SavedConnection.FromFlat(saved)];

        var edited = new AiConnections();
        var editedKeys = new Dictionary<string, string>();
        foreach (SavedConnection one in stored)
        {
            AiConnection made = one.ToConnection();
            edited.Items.Add(made);
            if (one.ApiKey is { Length: > 0 }) editedKeys[made.Id] = one.ApiKey;
        }
        if (edited.Items.Count == 0) return new State(book, keys, fromConfig);

        edited.SelectedId = edited.Find(saved.SelectedId) is not null
            ? saved.SelectedId!
            : edited.Items[0].Id;

        // A saved file with no key falls back to the configured one rather than turning the
        // feature off: changing the model in the browser must not unset a key the compose file
        // supplied. It attaches to the first stored connection, which is the one the configured
        // connection became.
        var editedFromConfig = new HashSet<string>();
        if (_configured.ApiKey.Length > 0 && !editedKeys.ContainsKey(edited.Items[0].Id))
        {
            editedKeys[edited.Items[0].Id] = _configured.ApiKey;
            editedFromConfig.Add(edited.Items[0].Id);
        }

        return new State(edited, editedKeys, editedFromConfig);
    }

    /// <summary>
    /// The file's shape. <c>Connections</c> is what is written now; the flat fields beside it
    /// are what was written before there were several, and are read once and never again.
    /// </summary>
    private sealed record Saved(
        string? Provider, string? BaseUrl, string? Model,
        bool? ExtractTextLocally, int TimeoutSeconds, string? ApiKey, string? Effort = null,
        List<SavedConnection>? Connections = null, string? SelectedId = null);

    private sealed record SavedConnection(
        string? Id, string? Name, string? Provider, string? BaseUrl, string? Model,
        bool? ExtractTextLocally, int TimeoutSeconds, string? Effort, string? ApiKey)
    {
        public static SavedConnection FromFlat(Saved s) => new(
            null, null, s.Provider, s.BaseUrl, s.Model,
            s.ExtractTextLocally, s.TimeoutSeconds, s.Effort, s.ApiKey);

        public static SavedConnection Of(AiConnection c, string? key) => new(
            c.Id, c.Name, c.Provider.ToString(), c.BaseUrl, c.Model,
            c.ExtractTextLocally, c.TimeoutSeconds, c.Effort.ToString(), key);

        public AiConnection ToConnection()
        {
            var made = new AiConnection
            {
                Name = Name ?? "",
                BaseUrl = BaseUrl ?? "",
                Model = Model ?? "",
                ExtractTextLocally = ExtractTextLocally,
                TimeoutSeconds = TimeoutSeconds is >= 15 and <= 900 ? TimeoutSeconds : 300,
            };
            if (Id is { Length: > 0 }) made.Id = Id;
            if (Enum.TryParse(Provider, ignoreCase: true, out AiProvider p)) made.Provider = p;
            // Absent in a file written before this setting existed, which reads as Default —
            // the state the field was in for every request that file was written under.
            if (Enum.TryParse(Effort, ignoreCase: true, out AiEffort e)) made.Effort = e;
            return made;
        }
    }

    private Saved? Read()
    {
        try
        {
            string path = SettingsPath;
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json);
        }
        catch (Exception ex)
        {
            // A settings file that cannot be read must not stop the server starting; the
            // configured connection is a perfectly good answer on its own.
            _log.LogWarning(ex, "Could not read the saved AI settings; using configuration only.");
            return null;
        }
    }

    private void Save(State state)
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            string path = SettingsPath;

            // A key that came from the environment is not written back out: it already lives
            // somewhere the operator controls, and copying it into a file would quietly
            // create a second place to change it — and a second place to leak it from.
            List<SavedConnection> connections = [.. state.Book.Items.Select(c => SavedConnection.Of(
                c, state.FromConfiguration.Contains(c.Id) ? null
                   : state.Keys.TryGetValue(c.Id, out string? key) ? key : null))];

            var saved = new Saved(null, null, null, null, 300, null, null,
                                  connections, state.Book.SelectedId);

            File.WriteAllText(path, JsonSerializer.Serialize(saved, Json));

            // Owner-only, where there is an owner. Windows inherits the directory's ACL and
            // has no equivalent one-liner, so this is a no-op there rather than a wrong one.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            // The connections are already applied in memory and work for this run; failing to
            // persist them is worth a warning, not an error the user cannot act on.
            _log.LogWarning(ex, "Could not save the AI settings to {Path}.", SettingsPath);
        }
    }
}
