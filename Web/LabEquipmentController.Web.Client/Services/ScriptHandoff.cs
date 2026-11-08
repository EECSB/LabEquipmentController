namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// How a script the AI assistant put forward reaches an editor. The assistant is the gear's
/// window, in the layout, and the editors belong to the bench page, so the page says here, while
/// it is up, where a script for an instrument or a sequence goes. The desktop's MainForm does this
/// in one place, on the assistant's ScriptUsed.
/// </summary>
public sealed class ScriptHandoff
{
    /// <summary>
    /// Set by the bench page while it is up: put this script in the editor for this session, or in
    /// the multi-instrument editor when the session is null, and say why not when it cannot.
    /// </summary>
    public Func<string?, string, Task<string?>>? Take { get; set; }

    /// <summary>Hands a script to its editor, or says why it could not be: null when it went.</summary>
    public async Task<string?> HandAsync(string? sessionId, string script)
    {
        if (Take is not Func<string?, string, Task<string?>> take)
            return "The bench is not open in this tab, so there is no editor to put it in. Copy it from the card instead.";
        return await take(sessionId, script);
    }
}
