using System;
using System.Collections.Generic;
using System.Linq;

namespace LabEquipmentController;

/// <summary>
/// The AI connections a user has set up, and which of them is in use.
///
/// One was enough while a connection was a provider and a key. It stopped being enough as soon
/// as the model became a choice: reading a command out of a programming guide and working out
/// what a sequence of SCPI ought to be are different jobs, and the cheap fast model that is
/// right for the first is not the one you want for the second. So they are kept as a list, each
/// with its own provider, model, effort and key, and every window that spends one offers the
/// list rather than stating what it happens to be set to.
///
/// The selection is part of this rather than part of each window. Picking a connection in the
/// datasheet window is picking the connection — the same rule the PDF switch and the effort
/// setting are held to, and for the same reason: two places that can disagree about what is in
/// force is one place too many.
///
/// Keys are not here. Core stays portable and never touches Windows crypto or a server's file
/// store; each build holds its own keys against <see cref="AiConnection.Id"/>.
/// </summary>
public sealed class AiConnections
{
    /// <summary>Every connection, in the order they were added.</summary>
    public List<AiConnection> Items { get; set; } = [];

    /// <summary>Which one is in use, by <see cref="AiConnection.Id"/>.</summary>
    public string SelectedId { get; set; } = "";

    /// <summary>
    /// The connection in use, or the first one, or null if there are none.
    ///
    /// Falling back rather than returning null for a stale id: a connection can be deleted from
    /// one window while another still holds its id, and the answer to "which one now" is the one
    /// that is left rather than "none of them".
    /// </summary>
    public AiConnection? Selected =>
        Items.FirstOrDefault(c => c.Id == SelectedId) ?? Items.FirstOrDefault();

    /// <summary>Find one by id, or null.</summary>
    public AiConnection? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : Items.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Add a connection on a provider's defaults, select it, and hand it back.
    ///
    /// Selected because adding one is how you say you want to use it — an addition that left the
    /// old one in force would be a button that appears to do nothing.
    /// </summary>
    public AiConnection Add(AiProvider provider = AiProvider.Gemini)
    {
        var made = new AiConnection { Provider = provider };
        Items.Add(made);
        SelectedId = made.Id;
        return made;
    }

    /// <summary>
    /// Forget one. The selection moves to whatever is left, and the last one can go: a bench with
    /// no AI connection on it is the state this feature starts in, so it must be reachable again.
    /// </summary>
    public bool Remove(string id)
    {
        AiConnection? found = Find(id);
        if (found is null) return false;

        Items.Remove(found);
        if (SelectedId == id) SelectedId = Items.FirstOrDefault()?.Id ?? "";
        return true;
    }

    /// <summary>Use this one, if it is one of these.</summary>
    public bool Select(string? id)
    {
        if (Find(id) is null) return false;
        SelectedId = id!;
        return true;
    }

    /// <summary>
    /// Names that tell two connections apart.
    ///
    /// A connection is called what the user called it, or after the model it reaches. That is
    /// enough until two of them answer to the same thing, at which point the provider is added —
    /// which separates two connections named "fast" on different services — and if they are still
    /// the same, a number. A picker whose entries cannot be told apart is a picker that cannot be
    /// used, and this is cheaper than making the user name everything.
    ///
    /// The number counts within the group that shares the name rather than along the whole list,
    /// so deleting an unrelated connection does not renumber the ones that are left more than it
    /// has to. Two identical unnamed connections really are indistinguishable, and a name is the
    /// way out of that; this only has to keep the list usable until someone types one.
    /// </summary>
    public IReadOnlyList<string> Labels()
    {
        var labels = new List<string>(Items.Count);
        var seen = new Dictionary<string, int>();

        foreach (AiConnection c in Items)
        {
            string name = c.EffectiveName;
            if (Items.Count(o => o.EffectiveName == name) > 1)
            {
                string withProvider = $"{name} · {c.Info.Label}";
                if (Items.Count(o => WithProvider(o) == withProvider) > 1)
                {
                    seen[name] = seen.TryGetValue(name, out int n) ? n + 1 : 1;
                    name += $" #{seen[name]}";
                }
                else name = withProvider;
            }
            labels.Add(name);
        }
        return labels;

        static string WithProvider(AiConnection c) => $"{c.EffectiveName} · {c.Info.Label}";
    }

    /// <summary>
    /// A list built from whatever was stored before there were lists.
    ///
    /// The settings file of every user who had this app before this change holds one connection
    /// and one key. Reading it as the first entry of a list — with its id generated then and
    /// written back — is what stops the change costing them their key.
    /// </summary>
    public static AiConnections From(AiConnection? single)
    {
        var made = new AiConnections();
        if (single is not null)
        {
            if (string.IsNullOrWhiteSpace(single.Id)) single.Id = Guid.NewGuid().ToString("N");
            made.Items.Add(single);
            made.SelectedId = single.Id;
        }
        return made;
    }

    /// <summary>A copy that can be edited without touching this one.</summary>
    public AiConnections Clone() => new()
    {
        Items = [.. Items.Select(c => c.Clone())],
        SelectedId = SelectedId,
    };
}
