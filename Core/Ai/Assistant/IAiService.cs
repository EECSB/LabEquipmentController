using System;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>
/// Everything the assistant window stands on, behind one interface: the AI guide's
/// <c>IAiService</c>, as GDS Viewer has it. The saved connections and the one picked, a provider
/// for a question, and where the chats are kept. **A window asks for this and never for a store or
/// a provider directly**, so the desktop's window and the page's differ only in what they are
/// handed:
///
/// - the desktop's (<c>DesktopAiService</c>): the connections and their keys from the settings
///   file, the keys under DPAPI, the model called straight from the app, the chats in a folder
///   under %AppData%;
/// - the page's (<c>WebAiService</c>): the server's connections, keyless, the model called through
///   the server's relay, which holds the key and pins the address and model, and the chats kept by
///   the server beside its other settings.
///
/// The bench itself is not here: what the tools work on is the window's (a
/// <see cref="SessionBench"/> on the desktop, the server's sessions on the page), and an app that
/// hosted this assistant would hand its own bench to the window the same way.
/// </summary>
public interface IAiService
{
    /// <summary>Where the chats are kept, as the window says it: "on this computer", "by the bench's server".</summary>
    string KeptWhere { get; }

    /// <summary>
    /// The saved connections, in the order AI Connection lists them, with the one picked — a copy,
    /// so the window can label and pick from it while AI Connection edits its own.
    /// </summary>
    Task<AiConnections> LoadConnectionsAsync();

    /// <summary>Makes a connection the picked one, as AI Connection's own list does, so both say the same.</summary>
    Task SelectAsync(string id);

    /// <summary>Why a question cannot go to this connection — no key kept for a provider that needs one — or null.</summary>
    string? WhyNotAsk(AiConnection connection);

    /// <summary>A provider for one question, at this effort ("" for the model's default).</summary>
    ILlmProvider CreateProvider(AiConnection connection, string effort);

    /// <summary>Where the chats are kept.</summary>
    IChatStore Chats { get; }

    /// <summary>Raised after the saved connections change — AI Connection applied — so the window picks again.</summary>
    event Func<Task>? ConnectionsChanged;
}
