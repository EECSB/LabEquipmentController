using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// One console's command history, with Up/Down recall. The index sits one past the
/// newest entry (the "typing a new command" slot), so the first Up recalls the last
/// command and stepping back down past the newest entry clears the input again.
/// </summary>
public sealed class CommandHistory
{
    private readonly List<string> _items = new();
    private int _index;

    public IReadOnlyList<string> Items => _items;
    public int Count => _items.Count;

    public void Add(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return;
        _items.Add(command);
        _index = _items.Count;
    }

    /// <summary>
    /// Step back (-1) or forward (+1) through the history and return the entry landed on,
    /// or "" for the empty slot past the newest command.
    /// </summary>
    public string Recall(int direction)
    {
        if (_items.Count == 0) return "";
        _index = Math.Clamp(_index + direction, 0, _items.Count);
        return _index < _items.Count ? _items[_index] : "";
    }
}

/// <summary>
/// One open connection to one instrument: the transport client, what the instrument turned
/// out to be, and the per-connection state that goes with it.
///
/// This exists because the app used to assume a single connection — client, profile,
/// identity and history were all fields on the main form. With one console per instrument
/// there is one of these per tab instead.
/// </summary>
public sealed class InstrumentSession : IDisposable
{
    public InstrumentSession(IInstrumentClient client, string? identity,
                             InstrumentProfile profile, int timeoutMs)
    {
        Client = client;
        Identity = identity ?? "";
        Profile = profile;
        UserTimeoutMs = timeoutMs;
    }

    public IInstrumentClient Client { get; }

    /// <summary>The instrument's *IDN? reply, or "" if it never gave one.</summary>
    public string Identity { get; }

    /// <summary>Quick commands / capture support inferred from <see cref="Identity"/>.</summary>
    public InstrumentProfile Profile { get; }

    public string Host => Client.Host;

    public bool IsConnected => Client.IsConnected;

    /// <summary>
    /// The Timeout field's value at connect time. Long transfers (screen / waveform
    /// capture) raise <see cref="IInstrumentClient.TimeoutMs"/> temporarily and restore
    /// it from here afterwards.
    /// </summary>
    public int UserTimeoutMs { get; set; }

    /// <summary>True while a script is driving this link — the console locks itself out
    /// meanwhile, since two request/response streams on one connection would collide.</summary>
    public bool IsBusy { get; set; }

    public CommandHistory History { get; } = new();

    /// <summary>Short caption for this session's tab, e.g. "DS2202A (192.168.1.17)".</summary>
    public string Title
    {
        get
        {
            (_, string model) = InstrumentProfile.ParseIdentity(Identity);
            return string.IsNullOrEmpty(model) ? $"Instrument ({Host})" : $"{model} ({Host})";
        }
    }

    /// <summary>
    /// One-line summary for the console header: where it is and what it is. The transport
    /// description already carries the port, so the address is given bare.
    /// </summary>
    public string Description
    {
        get
        {
            string head = $"{Host} — {Client.Description} — {Profile.Name}";
            return string.IsNullOrWhiteSpace(Identity) ? head : head + "  ·  " + Identity;
        }
    }

    /// <summary>
    /// Hand the front panel back, then drop the link. Best effort: an instrument that has
    /// already gone away must not stop the session from closing.
    /// </summary>
    public async Task CloseAsync()
    {
        try { await Client.ReturnToLocalAsync(); } catch { /* best effort */ }
        Client.Dispose();
    }

    /// <summary>Drop the link without waiting on the instrument. Prefer <see cref="CloseAsync"/>.</summary>
    public void Dispose() => Client.Dispose();
}

/// <summary>
/// The set of sessions the app currently has open, one per console.
///
/// The lookup by host is not a convenience: the Rigol DS2202 wedges its firmware if a
/// second TCP session is opened against it (front panel dead, power-cycle to recover), so
/// connecting to an address that already has a session must reuse it rather than dial again.
/// </summary>
public sealed class SessionRegistry
{
    private readonly List<InstrumentSession> _sessions = new();

    public IReadOnlyList<InstrumentSession> Sessions => _sessions;

    public int Count => _sessions.Count;

    public void Add(InstrumentSession session)
    {
        if (session == null) throw new ArgumentNullException(nameof(session));
        _sessions.Add(session);
    }

    public bool Remove(InstrumentSession session) => _sessions.Remove(session);

    /// <summary>The open session for this address, or null. Host match is case-insensitive
    /// so a hostname typed in a different case still finds its existing session.</summary>
    public InstrumentSession? FindByHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return null;
        foreach (InstrumentSession s in _sessions)
            if (string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }

    /// <summary>
    /// The session a sequence's <c>DEVICE gen : SDG2042X</c> line refers to, or null.
    ///
    /// Matched against the model from *IDN? first, then the address, so a sequence can name
    /// either. Model is the useful one — these instruments are on DHCP and their addresses
    /// move between sessions, while "SDG2042X" is written on the front of the box.
    ///
    /// The model match is a prefix, because vendors qualify the same instrument differently
    /// in *IDN? than on its label: an SDS2354X answers "SDS2354X Plus". Writing the short
    /// name in a script should find the longer one. Exact matches are preferred so that a
    /// bench holding both an SDM3055 and an SDM3055X cannot be resolved by luck.
    /// </summary>
    public InstrumentSession? FindForSequence(string? nameOrAddress)
    {
        if (string.IsNullOrWhiteSpace(nameOrAddress)) return null;
        string want = nameOrAddress.Trim();

        foreach (InstrumentSession s in _sessions)
            if (string.Equals(ModelOf(s), want, StringComparison.OrdinalIgnoreCase)) return s;

        InstrumentSession? prefix = null;
        foreach (InstrumentSession s in _sessions)
        {
            if (!ModelOf(s).StartsWith(want, StringComparison.OrdinalIgnoreCase)) continue;
            if (prefix != null) return null;   // ambiguous — refuse rather than pick one
            prefix = s;
        }
        if (prefix != null) return prefix;

        return FindByHost(want);
    }

    private static string ModelOf(InstrumentSession s)
        => InstrumentProfile.ParseIdentity(s.Identity).Model;
}
