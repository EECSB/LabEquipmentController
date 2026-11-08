using System.Collections.Generic;

namespace LabEquipmentController;

/// <summary>
/// One turn of a conversation, in the shape both request builders write from.
///
/// Only the two roles a caller can author are here. An assistant turn that called a tool carries
/// blocks of its provider's own — Anthropic's tool_use, OpenAI's tool_calls — which stay inside
/// the loop that produced them: replaying them from here would mean rebuilding each provider's
/// wire format from a copy that has lost half of it. What a later turn is given instead is what
/// was done, said in words, which is the conversation's to write (GDS Viewer's <c>AiChat</c>).
/// </summary>
public sealed class LlmMessage
{
    public const string UserRole = "user";
    public const string AssistantRole = "assistant";

    public LlmMessage(string role, string text, IReadOnlyList<AiAttachment>? attachments = null)
    {
        Role = role;
        Text = text ?? "";
        Attachments = attachments ?? [];
    }

    public string Role { get; }

    public string Text { get; }

    /// <summary>
    /// The files a question was sent with, which go ahead of its words — see
    /// <see cref="AiAttachment.Parts"/>. Empty for anything else.
    /// </summary>
    public IReadOnlyList<AiAttachment> Attachments { get; }

    public static LlmMessage FromUser(string text, IReadOnlyList<AiAttachment>? attachments = null)
        => new(UserRole, text, attachments);

    public static LlmMessage FromAssistant(string text) => new(AssistantRole, text);
}
