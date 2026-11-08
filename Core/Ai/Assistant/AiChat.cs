using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace LabEquipmentController;

/// <summary>One entry in the conversation, including the ones that are not something anybody said.</summary>
public sealed class ChatTurn
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";
    public const string Error = "error";

    /// <summary>
    /// A run of tool calls paused at its limit of rounds with nothing wrong: shown as the question
    /// whether to go on for another as many, with Continue on it rather than as an error, and left
    /// out of what the model is sent, as an error is — the calls before it go, said in words, with
    /// whatever is sent next.
    /// </summary>
    public const string Paused = "paused";

    /// <summary>
    /// A line from the app rather than from either side — the bench changed under the conversation,
    /// say — and where the conversation the model is sent starts again.
    /// </summary>
    public const string Note = "note";

    /// <summary>
    /// The briefing a compaction put in place of the conversation before it: sent as the person's
    /// message, with the files that conversation carried kept on it unticked, and where the
    /// conversation the model is sent starts. See <see cref="AiChat.CompactAsync"/>.
    /// </summary>
    public const string Summary = "summary";

    /// <summary>
    /// A line from the app where a kept chat was opened again: shown as a note, but **not where the
    /// conversation the model is sent starts** — carrying a chat on is the point of opening it. What
    /// it says goes to the model too, with <see cref="AiChat.ReopenedWarning"/>. See
    /// <see cref="AiChat.Restore"/>.
    /// </summary>
    public const string Reopened = "reopened";

    public required string Kind { get; init; }

    public string Text { get; set; } = "";

    /// <summary>The tool a tool turn asked to call.</summary>
    public string ToolName { get; init; } = "";

    /// <summary>What it was asked to call it with, as the model sent it.</summary>
    public string ArgumentsJson { get; init; } = "";

    /// <summary>Whether the call changes an instrument — what is worth showing differently from a read, since nothing on a bench is undone.</summary>
    public bool Changes { get; init; }

    /// <summary>
    /// Whether the call put a script forward rather than doing anything (<see cref="LlmTool.Proposes"/>):
    /// its card carries Use This Script, beside <see cref="Proposal"/>. It changes nothing, so it never
    /// waited for anybody.
    /// </summary>
    public bool Proposes { get; init; }

    /// <summary>Whether the rung answered for the person. The card is shown either way; only the wording differs.</summary>
    public bool Auto { get; set; }

    /// <summary>Null while the card is still waiting to be answered.</summary>
    public bool? Decision { get; set; }

    /// <summary>What the call answered. Null until it has, and on a call that was declined.</summary>
    public string? Result { get; set; }

    public bool ResultIsError { get; set; }

    /// <summary>The picture the call took — an instrument's screen — as base64, for the card to show what the model was shown.</summary>
    public string? Png { get; set; }

    /// <summary>
    /// The script a proposing call put forward (<see cref="LlmToolOutput.Proposal"/>), for its card to
    /// show beside Use This Script. Never replayed to the model whole: it wrote it, and is told how
    /// many lines it was and for whom.
    /// </summary>
    public string? Proposal { get; set; }

    /// <summary>How the waiting call is let go when one of the card's buttons is pressed.</summary>
    public TaskCompletionSource<bool>? Gate { get; set; }

    /// <summary>
    /// The files a question was sent with, in the order they were attached. Empty on every other kind
    /// of turn, and on a question that failed — see <see cref="AiChat.GaveFilesBack"/>.
    ///
    /// **Replaced rather than changed once the turn is on screen**: a failed question's files go back
    /// to the box by this turn being handed a new, empty list, so a window walking the old one on its
    /// own thread never meets a list changing under it.
    /// </summary>
    public List<AiAttachment> Attachments { get; internal set; } = [];

    /// <summary>What the run this turn ended was billed for, every request of it. Null on a turn that ended none, and where the provider said nothing.</summary>
    public LlmUsage? Usage { get; set; }

    /// <summary>The model that ran it, since the rates are a model's.</summary>
    public string Model { get; set; } = "";

    /// <summary>What <see cref="Usage"/> cost at the model's rates, or null where there is no rate for it.</summary>
    public decimal? Cost { get; set; }
}

/// <summary>How a message went, for the window to do what follows: put a failed question's words back with its files, say.</summary>
public enum ChatOutcome
{
    /// <summary>Nothing was sent: an answer was still coming, or there were no words.</summary>
    NotSent,

    Answered,

    /// <summary>A refusal or an error ended it.</summary>
    Failed,

    /// <summary>The calls reached the cap on rounds still going somewhere, and wait for Continue.</summary>
    Paused,

    /// <summary>Stop, or Clear, ended it.</summary>
    Stopped,
}

/// <summary>What one model has been billed for in this window.</summary>
public sealed class ModelSpend
{
    public LlmUsage Usage { get; } = new();

    /// <summary>What the priced part of it cost — null until an answer from it had a rate to price it at.</summary>
    public decimal? Cost { get; set; }

    /// <summary>A copy, for a reader on another thread: the original keeps being added to.</summary>
    internal ModelSpend Copy()
    {
        var copy = new ModelSpend { Cost = Cost };
        copy.Usage.Add(Usage);
        return copy;
    }
}

/// <summary>
/// The conversation behind the assistant window: what has been said, what has been run, and the run
/// in progress — and the files waiting to go with the next question, what it has all been billed
/// for, the tools switched off, and the briefings compacting it leaves.
///
/// GDS Viewer's <c>AiChat</c>, with its names and shapes, so the windows written against one read
/// the other. **A plain class rather than the window's own state**, which is GDS Viewer's one
/// structural change from GraphDBViewer: a whole conversation — approvals, declines, a stop with a
/// card waiting — can then be driven by a test, with a stand-in provider and a fake bench, and no
/// window at all.
///
/// **Safe to share between threads**, which GDS Viewer, in a single-threaded page, never needed to
/// be. The providers and the gate await as library code does, so in the desktop window a call
/// arrives, waits and lands on a pool thread while the window's thread draws. So every change to the
/// turns, the files waiting, the spend and the run's state is made under one lock; what is read
/// from outside — <see cref="Turns"/>, <see cref="Pending"/>, <see cref="Spend"/> — is a copy taken
/// under it; and <see cref="Changed"/> is raised outside it, from whichever thread the change was made
/// on. The lists the host is handed are its own, and no later change reaches them; a turn's own fields
/// — a card's answer, its result — are set under the same lock, and a window reading them after
/// Changed sees at worst the state just before the change it is about to hear of. The page is
/// single-threaded, so it costs the page nothing.
/// </summary>
public sealed class AiChat
{
    /// <summary>The most of a call's arguments and of its answer replayed into later messages — enough to say what was done, short of a trace's every point.</summary>
    private const int MostReplayed = 600;

    /// <summary>
    /// How much history every question has to be re-sending before compacting it is suggested, in
    /// tokens — ConfigurationRuleEditor's default, which GDS Viewer kept. Counted over the
    /// conversation alone, files included: a short conversation about one big file, measured with the
    /// instructions and the tools, tripped it where compacting saves nothing.
    /// </summary>
    public const long CompactAfterTokens = 20_000;

    /// <summary>
    /// How many questions have to have been asked since the conversation began, was compacted or
    /// started afresh before compacting it is suggested — so one question with a big file does not
    /// trip it on its own. That file is better left out in the files panel than summarised while the
    /// next question may still need it.
    /// </summary>
    public const int QuestionsBeforeCompacting = 3;

    /// <summary>About how long a briefing comes back, in tokens, for pricing a compaction before it runs.</summary>
    public const long BriefingTokens = 2_000;

    /// <summary>
    /// What a compaction asks for: a briefing the assistant can carry on from, with the things a
    /// conversation about a bench turns on kept exactly. ConfigurationRuleEditor's CompactSystem, by
    /// way of GDS Viewer's for layouts, where shape numbers had to survive; here it is the instruments'
    /// names, which are what every call names, and the commands and readings, which a briefing that
    /// rounded 4.99876E+00 to "about 5 V" would have quietly changed.
    /// </summary>
    public const string CompactSystem =
        "You compress a conversation between a person and the AI assistant in Lab Equipment Controller, which works the "
        + "instruments on a lab bench through tools, so that it can go on in a smaller context. Write a briefing for the "
        + "assistant that picks the conversation up: what the person is working on; the instruments, by the names the tools "
        + "gave them - dmm, psu, scope - with their models and addresses; every command sent and every value read, exactly "
        + "as it was written and never rounded or paraphrased - 4.99876E+00 stays 4.99876E+00; what the person declined and "
        + "what a tool refused, and why; errors the instruments reported; the scripts proposed, what each does and for which "
        + "instrument; and what is still open. Where an attached file mattered, carry over the specific facts that were used "
        + "from it, not the file. Plain prose under short headings, no preamble and no closing remarks, and length in "
        + "proportion to what happened: a short conversation compresses to a short briefing.";

    /// <summary>How a briefing is introduced when it is sent, since nobody typed it.</summary>
    public const string SummaryPreface = "Summary of the earlier conversation, compacted to save tokens:";

    /// <summary>
    /// What a reopened chat tells the model after the app's own line. The names the tools gave the
    /// instruments are given at every list, from what is open then — where GDS Viewer's shape numbers
    /// belonged to a session that was gone — so the same name may be another instrument now, or none.
    /// </summary>
    public const string ReopenedWarning =
        "The instrument names used above (dmm, psu, scope...) were given in an earlier session, and may no longer be "
        + "open or name the same instrument - list the instruments again before using one.";

    private readonly object _sync = new();
    private readonly List<ChatTurn> _turns = [];
    private readonly List<AiAttachment> _pending = [];
    private readonly Dictionary<string, ModelSpend> _spend = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cancel;

    /// <summary>The card a call is waiting on, so what it answered goes on the right one. One is enough: both providers run calls one after another.</summary>
    private ChatTurn? _pendingTool;

    /// <summary>Counts conversations, so a run stopped by Clear does not write its ending into the one that replaced it.</summary>
    private int _generation;

    private ToolApprovalMode _mode = ToolApprovalMode.AutoRead;
    private bool _generating;
    private bool _compacting;
    private bool _gaveFilesBack;
    private bool _compactSuggestionDismissed;

    /// <summary>The conversation as it stands, a copy: the run goes on adding to the conversation, never to this.</summary>
    public IReadOnlyList<ChatTurn> Turns
    {
        get
        {
            lock (_sync) return [.. _turns];
        }
    }

    public bool Generating
    {
        get
        {
            lock (_sync) return _generating;
        }
    }

    /// <summary>Whether what is running is a compaction rather than an answer — see <see cref="CompactAsync"/>.</summary>
    public bool Compacting
    {
        get
        {
            lock (_sync) return _compacting;
        }
    }

    /// <summary>
    /// The tools switched off in the window's tool picker, by name: not offered to the model, refused
    /// if it calls one anyway, and named in its instructions so it says what it would have used rather
    /// than that it cannot — hand it to <see cref="BenchToolRunner.Off"/> and
    /// <see cref="AssistantPrompt.Build"/>. **Kept through Clear and through the conversation starting
    /// afresh**, as ConfigurationRuleEditor keeps its picker's choices: somebody who switched a tool off
    /// wants it off in the next conversation too. Changed between answers, never during one.
    /// </summary>
    public HashSet<string> ToolsOff { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the suggestion to compact was put away. It comes back after the next compaction, and in a new conversation.</summary>
    public bool CompactSuggestionDismissed
    {
        get
        {
            lock (_sync) return _compactSuggestionDismissed;
        }
        set
        {
            lock (_sync) _compactSuggestionDismissed = value;
        }
    }

    /// <summary>
    /// The files waiting to go with the next question, in the order they were attached — the chips
    /// over the box — as a copy. Kept through Clear and through the conversation starting afresh, as
    /// the words in the box are.
    /// </summary>
    public IReadOnlyList<AiAttachment> Pending
    {
        get
        {
            lock (_sync) return [.. _pending];
        }
    }

    /// <summary>
    /// Whether the last message failed with files, and gave them back to the box.
    ///
    /// **A file the provider refused would be refused again with every later question**, since each
    /// one sends the conversation whole — a PDF past its page limit, a picture it cannot read. So a
    /// question that fails takes its files off itself and puts them back where one can be taken off
    /// before asking again, which is ConfigurationRuleEditor's rule; the window puts the question's
    /// words back with them when the box is empty. A stop is no sign that a file was refused, and
    /// keeps them where they were.
    /// </summary>
    public bool GaveFilesBack
    {
        get
        {
            lock (_sync) return _gaveFilesBack;
        }
    }

    /// <summary>
    /// What has been billed, by model, since the window opened, as a copy. **Clear does not take it
    /// back**: what was spent was spent, and a figure that went to nothing with every new conversation
    /// would hide what a day of them came to.
    /// </summary>
    public IReadOnlyDictionary<string, ModelSpend> Spend
    {
        get
        {
            lock (_sync) return _spend.ToDictionary(pair => pair.Key, pair => pair.Value.Copy(), StringComparer.Ordinal);
        }
    }

    /// <summary>Everything billed, every model together.</summary>
    public LlmUsage Used
    {
        get
        {
            var used = new LlmUsage();
            lock (_sync)
            {
                foreach (ModelSpend model in _spend.Values) used.Add(model.Usage);
            }
            return used;
        }
    }

    /// <summary>What the priced part of <see cref="Used"/> cost, or null until something had a rate to be priced at.</summary>
    public decimal? Cost
    {
        get
        {
            decimal? total = null;
            lock (_sync)
            {
                foreach (ModelSpend model in _spend.Values)
                {
                    if (model.Cost is decimal dollars) total = (total ?? 0m) + dollars;
                }
            }
            return total;
        }
    }

    /// <summary>The bytes of every file the conversation still sends with each question: the ticked ones since it last started afresh.</summary>
    public long SentBytes
    {
        get
        {
            lock (_sync) return SentBytesNow();
        }
    }

    public long PendingBytes
    {
        get
        {
            lock (_sync) return PendingBytesNow();
        }
    }

    /// <summary>How many rounds of tool calls a message may take before the run pauses to ask — the person's setting, where the window has one.</summary>
    public int MaxToolRounds { get; set; } = LlmProviders.DefaultToolRounds;

    /// <summary>
    /// How far the model may go on its own.
    ///
    /// **Reads run, changes ask, to start with.** A read only looks at an instrument, and a model that
    /// has to stop and be waved through each look is one nobody uses; a send still waits, and on a
    /// bench that is the card an output switched on into a circuit is stopped at. The rung that sends
    /// unasked is never where the window starts, so a restart never finds it on.
    ///
    /// **In force from the moment it is chosen**, in the middle of an answer too: every call reads it
    /// as it is made, and a card already waiting that the new rung would not have stopped for is let
    /// go — approved, and marked as run without asking. GDS Viewer read it once as a run started, and a
    /// switch to "change without asking" went on putting up cards until the answer was finished. A rung
    /// that asks more leaves a waiting card waiting, and only stops the calls after it.
    /// </summary>
    public ToolApprovalMode Mode
    {
        get
        {
            lock (_sync) return _mode;
        }
        set
        {
            lock (_sync)
            {
                _mode = value;

                if (_pendingTool is not ChatTurn waiting || waiting.Decision is not null || waiting.Gate is null) return;
                if (ApprovingToolRunner.NeedsApproval(value, waiting.Changes)) return;

                // Marked only when this is what let it go, rather than a button pressed a moment before.
                if (waiting.Gate.TrySetResult(true)) waiting.Auto = true;
            }
        }
    }

    /// <summary>
    /// Raised whenever the conversation changes, so the window can draw it again — **from whichever
    /// thread the change was made on**, which during a run is a pool thread. A window that draws on a
    /// thread of its own marshals to it; the conversation is never locked while this is raised, so a
    /// host that waits there for its own thread cannot deadlock against it.
    /// </summary>
    public event Func<Task>? Changed;

    /// <summary>
    /// Says something and waits for the answer: with the runner's tools, through the approval gate, or
    /// with none.
    ///
    /// The person's words are added before anything is sent, so they are on screen at once; the answer,
    /// a refusal or a stop is added when it comes. Nothing thrown gets out of the run — a failure is a
    /// turn that says what went wrong.
    ///
    /// **The files waiting in the box go with it**, and the box is left empty for the next question.
    /// **What the run was billed for is counted however it ends** — answered, refused, stopped or
    /// cleared away — since each request of it was billed as it was answered; the turn that ends it
    /// carries the figure.
    /// </summary>
    public async Task<ChatOutcome> SendAsync(string text, ILlmProvider provider, string systemPrompt, ILlmToolRunner? tools)
    {
        ChatTurn question;
        List<LlmMessage> conversation;
        int asOf;
        int rounds;
        var source = new CancellationTokenSource();

        lock (_sync)
        {
            if (_generating || string.IsNullOrWhiteSpace(text))
            {
                source.Dispose();
                return ChatOutcome.NotSent;
            }

            question = new ChatTurn { Kind = ChatTurn.User, Text = text.Trim(), Attachments = [.. _pending] };
            _pending.Clear();
            _turns.Add(question);

            conversation = BuildConversation();
            asOf = _generation;
            rounds = MaxToolRounds;
            _generating = true;
            _gaveFilesBack = false;
            _cancel = source;
        }

        LlmUsage before = provider.Spent.Copy();
        bool sentAnything = false;
        var outcome = ChatOutcome.Stopped;
        ChatTurn? ending = null;

        try
        {
            await RaiseChangedAsync().ConfigureAwait(false);

            LlmResult result;
            if (tools is null)
            {
                result = await provider.CompleteAsync(systemPrompt, conversation, source.Token).ConfigureAwait(false);
            }
            else
            {
                // The rung as it is when each call is made, not as it was when this began — see Mode.
                var approving = new ApprovingToolRunner(
                    tools,
                    (request, ct) => ApproveAsync(request, ct, asOf),
                    async (request, output) =>
                    {
                        // A send whose instrument complained after it still reached the instrument,
                        // so an error answer counts as sent here, where GDS Viewer's edit that failed
                        // had changed nothing.
                        if (output is not null && request.Changes) sentAnything = true;
                        await CompletedAsync(output, asOf).ConfigureAwait(false);
                    },
                    () => Mode);

                result = await provider.CompleteWithToolsAsync(systemPrompt, conversation, approving, source.Token, rounds)
                                       .ConfigureAwait(false);
            }

            lock (_sync)
            {
                if (asOf != _generation) return ChatOutcome.Stopped;

                if (result.IsError)
                {
                    ending = new ChatTurn { Kind = ChatTurn.Error, Text = result.Error + SentStays(sentAnything) };
                    outcome = ChatOutcome.Failed;
                }
                else if (result.IsPaused)
                {
                    ending = new ChatTurn { Kind = ChatTurn.Paused, Text = result.Text };
                    outcome = ChatOutcome.Paused;
                }
                else
                {
                    ending = new ChatTurn { Kind = ChatTurn.Assistant, Text = result.Text };
                    outcome = ChatOutcome.Answered;
                }

                _turns.Add(ending);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = ChatOutcome.Stopped;

            lock (_sync)
            {
                if (asOf == _generation)
                {
                    ending = new ChatTurn { Kind = ChatTurn.Error, Text = "Stopped." + SentStays(sentAnything) };
                    _turns.Add(ending);
                }
            }
        }
        catch (Exception failed)
        {
            outcome = ChatOutcome.Failed;

            lock (_sync)
            {
                if (asOf == _generation)
                {
                    ending = new ChatTurn { Kind = ChatTurn.Error, Text = failed.Message };
                    _turns.Add(ending);
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                Record(provider, before, ending);

                if (asOf == _generation)
                {
                    _generating = false;
                    _pendingTool = null;

                    if (outcome == ChatOutcome.Failed) GiveFilesBack(question);
                }

                if (ReferenceEquals(_cancel, source)) _cancel = null;
            }

            source.Dispose();
            await RaiseChangedAsync().ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>
    /// Asks the model for a briefing that can stand in for the conversation so far, and puts it in its
    /// place — see <see cref="ChatTurn.Summary"/>. ConfigurationRuleEditor's compaction, by way of GDS
    /// Viewer, which is there because a conversation only grows: every question sends all of it again,
    /// its files included.
    ///
    /// **One request, no tools**: the transcript — questions, answers, and each call with what came of
    /// it — under <see cref="CompactSystem"/>, with the files still ticked, on the provider given. A
    /// connection here is one somebody set up with their own key, so the one they picked is the one
    /// asked; the window makes it at the lowest effort the model takes, since a briefing needs care with
    /// names rather than reasoning.
    ///
    /// **What was folded away stays on screen and is not sent** — the briefing is where the
    /// conversation the model is sent starts — and its files stay on the briefing, unticked: what was
    /// used from them is in the briefing now, and sending them with every question after it would go on
    /// paying for what compacting stops paying for. **On any failure — an error, a refusal, Stop — the
    /// conversation is as it was**, with a line saying so; what the request was billed for is counted
    /// either way.
    /// </summary>
    public async Task<ChatOutcome> CompactAsync(ILlmProvider provider)
    {
        List<LlmMessage> request;
        int asOf;
        var source = new CancellationTokenSource();

        lock (_sync)
        {
            if (WhyNotCompact() is not null)
            {
                source.Dispose();
                return ChatOutcome.NotSent;
            }

            AiAttachment[] ticked = [.. SentFilesNow().Where(sent => !sent.File.Excluded).Select(sent => sent.File)];
            request = [LlmMessage.FromUser(CompactionAsk(), ticked)];
            asOf = _generation;
            _generating = true;
            _compacting = true;
            _cancel = source;
        }

        LlmUsage before = provider.Spent.Copy();
        var outcome = ChatOutcome.Stopped;
        ChatTurn? ending = null;

        try
        {
            await RaiseChangedAsync().ConfigureAwait(false);

            LlmResult result = await provider.CompleteAsync(CompactSystem, request, source.Token).ConfigureAwait(false);
            bool briefed = !result.IsError && !string.IsNullOrWhiteSpace(result.Text);

            // Another of every file the folded part carried, made out here: a copy waits on a file's
            // words if another thread is reading them, and nothing should wait inside the lock.
            List<AiAttachment> kept = briefed ? [.. SentFiles().Select(sent => sent.File.Unticked())] : [];

            lock (_sync)
            {
                if (asOf != _generation) return ChatOutcome.Stopped;

                if (briefed)
                {
                    ending = new ChatTurn { Kind = ChatTurn.Summary, Text = result.Text.Trim(), Attachments = kept };
                    outcome = ChatOutcome.Answered;
                    _compactSuggestionDismissed = false;
                }
                else
                {
                    string why = result.IsError ? result.Error : "The model wrote no briefing.";
                    ending = new ChatTurn { Kind = ChatTurn.Error, Text = $"The conversation could not be compacted, and is as it was. {why}" };
                    outcome = ChatOutcome.Failed;
                }

                _turns.Add(ending);
            }
        }
        catch (OperationCanceledException)
        {
            outcome = ChatOutcome.Stopped;

            lock (_sync)
            {
                if (asOf == _generation)
                {
                    ending = new ChatTurn { Kind = ChatTurn.Error, Text = "Compacting was stopped. The conversation is as it was." };
                    _turns.Add(ending);
                }
            }
        }
        catch (Exception failed)
        {
            outcome = ChatOutcome.Failed;

            lock (_sync)
            {
                if (asOf == _generation)
                {
                    ending = new ChatTurn { Kind = ChatTurn.Error, Text = $"The conversation could not be compacted, and is as it was. {failed.Message}" };
                    _turns.Add(ending);
                }
            }
        }
        finally
        {
            lock (_sync)
            {
                Record(provider, before, ending);

                if (asOf == _generation)
                {
                    _generating = false;
                    _compacting = false;
                }

                if (ReferenceEquals(_cancel, source)) _cancel = null;
            }

            source.Dispose();
            await RaiseChangedAsync().ConfigureAwait(false);
        }

        return outcome;
    }

    /// <summary>Why the conversation cannot be compacted now, or null when it can.</summary>
    public string? WhyNotCompact()
    {
        lock (_sync)
        {
            if (_generating) return "Not while an answer is coming.";
            if (QuestionsSinceAfresh > 0) return null;

            int from = Afresh();
            return from < _turns.Count && _turns[from].Kind == ChatTurn.Summary
                ? "Nothing has been asked since it was last compacted."
                : "There is nothing to compact yet.";
        }
    }

    /// <summary>The questions asked since the conversation began, was last compacted, or started afresh.</summary>
    public int QuestionsSinceAfresh
    {
        get
        {
            lock (_sync)
            {
                int questions = 0;
                for (int i = Afresh(); i < _turns.Count; i++)
                {
                    if (_turns[i].Kind == ChatTurn.User) questions++;
                }
                return questions;
            }
        }
    }

    /// <summary>
    /// About how many tokens of history every question sends again — the part a compaction replaces:
    /// the conversation since it last started afresh, with its ticked files as
    /// <paramref name="connection"/> is sent them, and not the instructions or the tools, which go with
    /// every question whatever is done to it.
    /// </summary>
    public long HistoryTokens(AiConnection connection)
        => BuildConversation().Sum(message => AiEstimate.Message(message, connection));

    /// <summary>
    /// Whether to suggest compacting: enough has been asked since the conversation began or was last
    /// compacted, and the history every question sends again has passed
    /// <see cref="CompactAfterTokens"/>. A compaction costs a reading of that history, so it is only
    /// worth it where there is a lot to stop sending and a conversation likely to go on.
    /// </summary>
    public bool WorthCompacting(AiConnection connection)
    {
        lock (_sync)
        {
            if (_generating || _compactSuggestionDismissed || QuestionsSinceAfresh < QuestionsBeforeCompacting) return false;
        }
        return HistoryTokens(connection) >= CompactAfterTokens;
    }

    /// <summary>What compacting would cost at <paramref name="price"/>: the history and the instructions read once, and a briefing written. Null where there is no rate.</summary>
    public static decimal? CompactCost(LlmPrice? price, long historyTokens)
        => price?.Cost(new LlmUsage
        {
            InputTokens = historyTokens + AiEstimate.TokensIn(CompactSystem.Length),
            OutputTokens = BriefingTokens,
            Requests = 1,
        });

    /// <summary>
    /// About how many tokens the next request would carry, from characters, before anything is sent:
    /// <paramref name="systemPrompt"/>, the definitions of <paramref name="tools"/>, the conversation as
    /// <see cref="BuildConversation"/> would replay it with its ticked files, and the box — the files
    /// waiting and the words <paramref name="typing"/> — each file as <paramref name="connection"/> is
    /// sent it. Both windows call it for the line under the box; every round of tool calls sends it
    /// again, and the line says so.
    /// </summary>
    public long EstimateTokens(string systemPrompt, IReadOnlyList<LlmTool> tools, AiConnection connection, string typing)
    {
        List<LlmMessage> conversation;
        lock (_sync)
        {
            conversation = BuildConversation();
            conversation.Add(LlmMessage.FromUser(typing ?? "", [.. _pending]));
        }

        return AiEstimate.Request(systemPrompt ?? "", AiEstimate.ToolTokens(tools), conversation, connection);
    }

    /// <summary>
    /// The conversation written out as text, to read back another day: every turn there is, the part a
    /// compaction folded away included, with what each call did, every script proposed in full, and
    /// what each answer was billed — ConfigurationRuleEditor's Export. The files are named, with their
    /// sizes, not included: they are still on the machine they came from.
    /// </summary>
    /// <param name="about">What the conversation was about, for its first line — the chat's title, the bench — or empty.</param>
    /// <param name="at">When it is written out.</param>
    public string Transcript(string about, DateTime at)
    {
        var text = new StringBuilder();
        text.AppendLine(string.IsNullOrWhiteSpace(about) ? "AI assistant conversation" : $"AI assistant conversation — {about.Trim()}");
        text.AppendLine($"Exported {at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");

        foreach (ChatTurn turn in Turns)
        {
            text.AppendLine();

            switch (turn.Kind)
            {
                case ChatTurn.User:
                    text.AppendLine("YOU");
                    text.AppendLine(turn.Text);
                    AppendFiles(text, "Attached", turn.Attachments);
                    break;

                case ChatTurn.Assistant:
                    text.AppendLine("ASSISTANT");
                    text.AppendLine(turn.Text);
                    break;

                case ChatTurn.Tool:
                    AppendCall(text, turn);
                    break;

                case ChatTurn.Summary:
                    text.AppendLine("SUMMARY OF THE ABOVE (compacted)");
                    text.AppendLine(turn.Text);
                    AppendFiles(text, "Kept from before it", turn.Attachments);
                    break;

                default:
                    text.AppendLine($"({turn.Text})");
                    break;
            }

            if (turn.Usage is not null) text.AppendLine($"  Billed: {UsageLine(turn)}, on {turn.Model}");
        }

        return text.ToString();
    }

    /// <summary>What a run was billed for, as one line: the requests it took, the tokens each way, and the cost where there is a rate.</summary>
    public static string UsageLine(ChatTurn turn)
    {
        if (turn.Usage is not LlmUsage used) return "";

        var line = new StringBuilder(used.Requests == 1 ? "1 request" : $"{used.Requests} requests");
        line.Append($" · {AiPricing.Count(used.InputTokens)} tokens in");
        if (used.CachedInputTokens > 0) line.Append($" ({AiPricing.Count(used.CachedInputTokens)} cached)");
        line.Append($" · {AiPricing.Count(used.OutputTokens)} out");
        if (turn.Cost is decimal dollars) line.Append($" · {AiPricing.Money(dollars)}");

        return line.ToString();
    }

    /// <summary>
    /// Puts a file in the box for the next question, or says why not: what the file is (see
    /// <see cref="AiAttachment.Problem"/>), how many one question carries, how much one conversation
    /// sends, and — last, as the check that reads a Word document through — whether its words can be
    /// read.
    ///
    /// **A PDF's words are not read here.** Whether it goes as them is the connection's to say, and a
    /// scan, which has none, goes whole to one that reads the file; so whether the connection picked
    /// can be sent it is the window's to ask as Send waits (<see cref="AiAttachment.NotSentTo"/>), since
    /// another connection may be picked before then.
    /// </summary>
    public string? Attach(AiAttachment file)
    {
        if (AiAttachment.Problem(file.Name, file.MediaType, file.Data) is string problem) return problem;

        lock (_sync)
        {
            if (WhyNoRoom(file) is string full) return full;
        }

        // Read out here, where a long document holds nobody up.
        if (file.Kind == AttachmentKind.Document && file.Unreadable is string unreadable) return unreadable;

        lock (_sync)
        {
            // Asked again: another file may have gone in while this one was read.
            if (WhyNoRoom(file) is string full) return full;

            _pending.Add(file);
            return null;
        }
    }

    /// <summary>Takes a file out of the box before it was sent.</summary>
    public void Unattach(AiAttachment file)
    {
        lock (_sync) _pending.Remove(file);
    }

    /// <summary>
    /// Ticks a file sent earlier back in, or unticks it, from the files panel — or says why it cannot be
    /// ticked. Unticking is always allowed: it is the way out of a conversation that can take no more.
    /// Ticking one in again is held to the same budget as attaching it was.
    /// </summary>
    public string? Include(AiAttachment file, bool include)
    {
        lock (_sync)
        {
            if (!include)
            {
                file.Excluded = true;
                return null;
            }

            if (!file.Excluded) return null;

            long total = SentBytesNow() + PendingBytesNow() + file.Data.Length;
            if (total > AiAttachment.MostConversationBytes)
                return $"{file.Name} would bring this conversation's files to {AiAttachment.Size(total)}, past the "
                     + $"{AiAttachment.Size(AiAttachment.MostConversationBytes)} one conversation can send. Untick another first.";

            file.Excluded = false;
            return null;
        }
    }

    /// <summary>
    /// Every file sent since the conversation last started afresh, with the question it went with — or
    /// the briefing that keeps it, after a compaction: what the files panel lists, ticked or not.
    /// </summary>
    public List<(ChatTurn Question, AiAttachment File)> SentFiles()
    {
        lock (_sync) return SentFilesNow();
    }

    /// <summary>Answers the card a call is waiting on.</summary>
    public void Decide(ChatTurn turn, bool approved) => turn.Gate?.TrySetResult(approved);

    /// <summary>Stops the run in progress. A card still waiting is answered no, which is what a stop is.</summary>
    public void Cancel()
    {
        CancellationTokenSource? running;
        lock (_sync) running = _cancel;

        // Cancelled outside the lock: what a cancellation sets going runs on this thread, and must be
        // free to come back for it.
        try
        {
            running?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run ended between the two, and there is nothing left to stop.
        }
    }

    /// <summary>
    /// Throws the conversation away and starts a new one.
    ///
    /// Stopped first: a run left going would add its answer to a conversation that no longer has the
    /// question in it. Its ending is not added at all — see <c>_generation</c> — where GraphDBViewer's
    /// Clear left a "Generation cancelled." behind in the empty window. The files waiting, the tools
    /// switched off and what has been spent stay, as they belong to the window rather than to a chat.
    /// </summary>
    public void Clear()
    {
        Cancel();

        lock (_sync)
        {
            _generation++;
            _turns.Clear();
            _pendingTool = null;
            _generating = false;
            _compacting = false;
            _compactSuggestionDismissed = false;
        }
    }

    /// <summary>
    /// Puts a kept chat back in place of the conversation, as <see cref="Clear"/> leaves it and with
    /// these turns — then, when there are any, a <see cref="ChatTurn.Reopened"/> line saying so, since
    /// the names the tools gave the instruments were given in a session that is gone. One line however
    /// often it is opened: a chat opened twice without a word in between does not say it twice. No line
    /// when there is nothing to say — a chat only put back to be written out as text.
    /// </summary>
    public void Restore(IEnumerable<ChatTurn> kept, string? reopened)
    {
        Clear();

        lock (_sync)
        {
            _turns.AddRange(kept);
            if (_turns.Count == 0 || reopened is null) return;

            if (_turns[^1].Kind == ChatTurn.Reopened) _turns[^1].Text = reopened;
            else _turns.Add(new ChatTurn { Kind = ChatTurn.Reopened, Text = reopened });
        }
    }

    /// <summary>
    /// Says why what was typed was not sent — a file the connection picked cannot take, say. A turn of
    /// its own that the model is never sent, since it is the window talking rather than either side of
    /// the conversation.
    /// </summary>
    public void Refuse(string why)
    {
        lock (_sync) _turns.Add(new ChatTurn { Kind = ChatTurn.Error, Text = why });
    }

    /// <summary>
    /// Marks where the conversation the model is sent starts again, with a line saying why — the bench
    /// changed so that what came before no longer describes it. What came before stays on screen, since
    /// somebody may still want to read it, but none of it is sent.
    /// </summary>
    public void StartAfresh(string why)
    {
        Cancel();

        lock (_sync)
        {
            _generation++;
            _pendingTool = null;
            _generating = false;
            _compacting = false;
            _compactSuggestionDismissed = false;

            if (_turns.Count > 0) _turns.Add(new ChatTurn { Kind = ChatTurn.Note, Text = why });
        }
    }

    /// <summary>
    /// The conversation as the model should see it: everything since the last note, or from the last
    /// briefing — which goes as the person's message, introduced as what it is, with the files kept on
    /// it.
    ///
    /// **What was run is replayed in words** rather than dropped. Its answer is what the model's next
    /// reply was built on, and neither provider's message list can carry a tool exchange across a
    /// request without rebuilding that provider's format from a copy of it — so each run is said
    /// plainly, as GraphDBViewer and GDS Viewer do. The calls of one answer go together in one turn,
    /// cut short where an argument or an answer runs long; a script proposed is said by its lines and
    /// for whom, never written out again to the model that wrote it.
    /// </summary>
    public List<LlmMessage> BuildConversation()
    {
        lock (_sync)
        {
            var conversation = new List<LlmMessage>();
            var ran = new StringBuilder();

            for (int i = Afresh(); i < _turns.Count; i++)
            {
                ChatTurn turn = _turns[i];

                if (turn.Kind == ChatTurn.Tool)
                {
                    AppendRun(ran, turn);
                    continue;
                }

                FlushRuns(conversation, ran);

                // A copy of a question's files, which a failed run hands back to the box from under it.
                switch (turn.Kind)
                {
                    case ChatTurn.User:
                        conversation.Add(LlmMessage.FromUser(turn.Text, [.. turn.Attachments]));
                        break;
                    case ChatTurn.Assistant:
                        conversation.Add(LlmMessage.FromAssistant(turn.Text));
                        break;
                    case ChatTurn.Summary:
                        conversation.Add(LlmMessage.FromUser($"{SummaryPreface}\n\n{turn.Text}", [.. turn.Attachments]));
                        break;
                    case ChatTurn.Reopened:
                        conversation.Add(LlmMessage.FromUser($"(From the app: {turn.Text} {ReopenedWarning})"));
                        break;
                }
            }

            FlushRuns(conversation, ran);
            return conversation;
        }
    }

    /// <summary>
    /// Where the turns a compaction folded away end: the last briefing's place, or 0 when there is none.
    /// The window shows what is before it only when asked, dimmed — it is still there to read, and none
    /// of it is sent.
    /// </summary>
    public int FoldedBefore
    {
        get
        {
            lock (_sync)
            {
                for (int i = _turns.Count - 1; i >= 0; i--)
                {
                    if (_turns[i].Kind == ChatTurn.Summary) return i;
                }
                return 0;
            }
        }
    }

    // --------------------------------------------------------------------- the run's callbacks

    /// <summary>
    /// Puts the model's call on screen, and waits for a button to answer it unless the rung already
    /// has. A call the rung let through still gets a card — one that runs unwatched is the one most
    /// worth seeing afterwards — it just does not stop for anybody.
    ///
    /// A run Clear has already let go of adds nothing to the conversation that replaced it, and runs
    /// nothing more: answered no, its card never shown. GDS Viewer, in a page with one thread, could
    /// not be asked between a Clear and the run seeing it; here a pool thread can.
    /// </summary>
    private async Task<bool> ApproveAsync(ToolRunRequest request, CancellationToken ct, int asOf)
    {
        var turn = new ChatTurn
        {
            Kind = ChatTurn.Tool,
            ToolName = request.ToolName,
            ArgumentsJson = request.ArgumentsJson,
            Changes = request.Changes,
            Proposes = request.Proposes,
        };
        TaskCompletionSource<bool>? gate = null;

        lock (_sync)
        {
            if (asOf != _generation) return false;

            if (request.AutoApproved)
            {
                turn.Auto = true;
                turn.Decision = true;
            }
            else
            {
                // Continuations run on their own, so a button's handler that answers the card does not
                // go on to run the rest of the provider's loop inside its own click.
                gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                turn.Gate = gate;
            }

            _turns.Add(turn);
            _pendingTool = turn;
        }

        await RaiseChangedAsync().ConfigureAwait(false);
        if (gate is null) return true;

        // A stop while a card is unanswered has to let it go, or the loop waits for a button nobody can
        // press. A stop is not a yes, so it answers the way declining does.
        using CancellationTokenRegistration registration = ct.Register(() => gate.TrySetResult(false));

        bool approved = await gate.Task.ConfigureAwait(false);

        lock (_sync) turn.Decision = approved;
        await RaiseChangedAsync().ConfigureAwait(false);

        return approved;
    }

    /// <summary>Writes what the call answered onto the card that asked for it — a proposal's script with it.</summary>
    private async Task CompletedAsync(LlmToolOutput? output, int asOf)
    {
        lock (_sync)
        {
            if (asOf != _generation) return;

            if (_pendingTool is ChatTurn card && output is not null)
            {
                card.Result = output.Text;
                card.ResultIsError = output.IsError;
                card.Png = output.Png;
                card.Proposal = output.Proposal;
            }

            _pendingTool = null;
        }

        await RaiseChangedAsync().ConfigureAwait(false);
    }

    /// <summary>Tells every listener, one after another, outside the lock.</summary>
    private async Task RaiseChangedAsync()
    {
        if (Changed is not Func<Task> changed) return;

        foreach (Func<Task> listener in changed.GetInvocationList().Cast<Func<Task>>())
            await listener().ConfigureAwait(false);
    }

    // ------------------------------------------------------- everything below runs under _sync

    /// <summary>Where the conversation the model is sent starts: just after the last note, at the last briefing, or at the beginning.</summary>
    private int Afresh()
    {
        int from = 0;
        for (int i = 0; i < _turns.Count; i++)
        {
            if (_turns[i].Kind == ChatTurn.Note) from = i + 1;
            else if (_turns[i].Kind == ChatTurn.Summary) from = i;
        }
        return from;
    }

    private List<(ChatTurn Question, AiAttachment File)> SentFilesNow()
    {
        var files = new List<(ChatTurn Question, AiAttachment File)>();
        for (int i = Afresh(); i < _turns.Count; i++)
        {
            foreach (AiAttachment file in _turns[i].Attachments) files.Add((_turns[i], file));
        }
        return files;
    }

    private long SentBytesNow() => SentFilesNow().Where(sent => !sent.File.Excluded).Sum(sent => (long)sent.File.Data.Length);

    private long PendingBytesNow() => _pending.Sum(file => (long)file.Data.Length);

    /// <summary>Why one more file cannot go in the box: how many a question carries, and how much a conversation sends.</summary>
    private string? WhyNoRoom(AiAttachment file)
    {
        if (_pending.Count >= AiAttachment.MostPerQuestion)
            return $"A question can carry {AiAttachment.MostPerQuestion} files at most, so {file.Name} was left out. Send these with a question first.";

        long total = SentBytesNow() + PendingBytesNow() + file.Data.Length;
        return total > AiAttachment.MostConversationBytes
            ? $"{file.Name} would bring this conversation's files to {AiAttachment.Size(total)}, past the "
            + $"{AiAttachment.Size(AiAttachment.MostConversationBytes)} one conversation can send — every question sends them "
            + "all again. Untick a file in the files panel, or clear the conversation."
            : null;
    }

    /// <summary>A failed question's files back in the box, ahead of any attached since — see <see cref="GaveFilesBack"/>.</summary>
    private void GiveFilesBack(ChatTurn question)
    {
        if (question.Attachments.Count == 0) return;

        _pending.InsertRange(0, question.Attachments);
        question.Attachments = [];
        _gaveFilesBack = true;
    }

    /// <summary>A run's share of what its provider was billed, put on the turn that ended it and added to <see cref="Spend"/>.</summary>
    private void Record(ILlmProvider provider, LlmUsage before, ChatTurn? ending)
    {
        LlmUsage used = provider.Spent.Since(before);
        if (!used.Reported) return;

        decimal? cost = provider.Price?.Cost(used);

        if (ending is not null)
        {
            ending.Usage = used;
            ending.Model = provider.Model;
            ending.Cost = cost;
        }

        if (!_spend.TryGetValue(provider.Model, out ModelSpend? spent))
        {
            spent = new ModelSpend();
            _spend[provider.Model] = spent;
        }

        spent.Usage.Add(used);
        if (cost is decimal dollars) spent.Cost = (spent.Cost ?? 0m) + dollars;
    }

    /// <summary>
    /// The one message of a compaction: the conversation since it last started afresh, as a transcript
    /// — an earlier briefing, the questions and answers, and each call with what came of it — and the
    /// ask. What was decided goes in with what was said: a briefing that lost a decline would walk the
    /// next answer into asking for the same send again.
    /// </summary>
    private string CompactionAsk()
    {
        var text = new StringBuilder();
        text.AppendLine("Here is the conversation to compress. The files still sent with it, if any, are above.");
        text.AppendLine();
        text.AppendLine("<conversation>");

        var ran = new StringBuilder();

        for (int i = Afresh(); i < _turns.Count; i++)
        {
            ChatTurn turn = _turns[i];

            if (turn.Kind == ChatTurn.Tool)
            {
                AppendRunOf(ran, turn);
                continue;
            }

            if (ran.Length > 0)
            {
                text.AppendLine("ASSISTANT'S TOOL CALLS:");
                text.Append(ran);
                text.AppendLine();
                ran.Clear();
            }

            switch (turn.Kind)
            {
                case ChatTurn.Summary:
                    text.AppendLine("SUMMARY LEFT BY AN EARLIER COMPACTION:").AppendLine(turn.Text).AppendLine();
                    break;
                case ChatTurn.User:
                    text.AppendLine("PERSON:").AppendLine(turn.Text).AppendLine();
                    break;
                case ChatTurn.Assistant:
                    text.AppendLine("ASSISTANT:").AppendLine(turn.Text).AppendLine();
                    break;
                case ChatTurn.Error or ChatTurn.Paused:
                    text.AppendLine($"(The answer ended there: {turn.Text})").AppendLine();
                    break;
            }
        }

        if (ran.Length > 0)
        {
            text.AppendLine("ASSISTANT'S TOOL CALLS:");
            text.Append(ran);
            text.AppendLine();
        }

        text.AppendLine("</conversation>");
        text.AppendLine();
        text.Append("Write the briefing now.");

        return text.ToString();
    }

    // ------------------------------------------------------------------ what a call is said as

    /// <summary>
    /// One call, replayed to the model that made it. On a bench what matters about a call is whether it
    /// reached the instrument, so a send declined is said never to have, and one that did not finish is
    /// said to be unknown — a stop can land after the command left and before its answer came back.
    /// </summary>
    private static void AppendRun(StringBuilder ran, ChatTurn turn)
    {
        if (turn.Decision is null) return;

        ran.Append(ran.Length == 0 ? "(What you did while answering that:" : "");
        ran.AppendLine();

        if (turn.Proposes)
        {
            string proposed = ProposalSaid(turn);
            ran.Append(turn.Decision == false ? $"- You proposed {proposed}, and the person declined it."
                     : turn.Result is null ? $"- You proposed {proposed}, and it did not finish."
                     : $"- You proposed {proposed}, and it answered: {Shortened(turn.Result)}");
            return;
        }

        string called = $"{turn.ToolName} {Shortened(turn.ArgumentsJson)}";

        if (turn.Decision == false)
            ran.Append(turn.Changes
                ? $"- You asked to call {called}, and the person declined, so it never reached the instrument."
                : $"- You asked to call {called}, and the person declined.");
        else if (turn.Result is null)
            ran.Append(turn.Changes
                ? $"- You called {called}, and it did not finish: whether it reached the instrument is not known - query the setting before relying on it."
                : $"- You called {called}, and it did not finish.");
        else if (turn.ResultIsError)
            ran.Append($"- You called {called}, and it came back as an error: {Shortened(turn.Result)}");
        else
            ran.Append($"- You called {called}, and it answered: {Shortened(turn.Result)}");
    }

    private static void FlushRuns(List<LlmMessage> conversation, StringBuilder ran)
    {
        if (ran.Length == 0) return;

        ran.Append(')');
        conversation.Add(LlmMessage.FromUser(ran.ToString()));
        ran.Clear();
    }

    /// <summary>One call in a compaction's transcript, said from outside rather than to the model that made it — a proposal with its script, which the briefing has to describe.</summary>
    private static void AppendRunOf(StringBuilder ran, ChatTurn turn)
    {
        if (turn.Proposes)
        {
            string proposed = ProposalSaid(turn);
            ran.AppendLine(turn.Decision == false ? $"- proposed {proposed}, and the person declined it."
                         : turn.Result is null ? $"- proposed {proposed}, and it did not finish."
                         : $"- proposed {proposed}, and it answered: {Shortened(turn.Result)}");
            if (ScriptOf(turn) is { Length: > 0 } script) ran.AppendLine($"  The script: {Shortened(script)}");
            return;
        }

        string called = $"{turn.ToolName} {Shortened(turn.ArgumentsJson)}";

        if (turn.Decision == false)
            ran.AppendLine(turn.Changes
                ? $"- asked to call {called}, and the person declined, so it never reached the instrument."
                : $"- asked to call {called}, and the person declined.");
        else if (turn.Decision is null || turn.Result is null)
            ran.AppendLine(turn.Changes
                ? $"- called {called}, and it did not finish: whether it reached the instrument is not known."
                : $"- called {called}, and it did not finish.");
        else if (turn.ResultIsError)
            ran.AppendLine($"- called {called}, and it came back as an error: {Shortened(turn.Result)}");
        else
            ran.AppendLine($"- called {called}, and it answered: {Shortened(turn.Result)}");
    }

    /// <summary>A call, in the transcript: what was asked for, and what came of it in full — a proposal with its whole script, which is the person's to keep.</summary>
    private static void AppendCall(StringBuilder text, ChatTurn turn)
    {
        if (turn.Proposes)
        {
            string proposed = ProposalSaid(turn);
            if (turn.Decision == false)
            {
                text.AppendLine($"  Proposed {proposed} — declined.");
                return;
            }
            if (turn.Decision is null || turn.Result is null)
            {
                text.AppendLine($"  Proposed {proposed} — it did not finish.");
                return;
            }

            text.AppendLine($"  Proposed {proposed} — it answered:");
            Indented(text, turn.Result, "    ");
            if (ScriptOf(turn) is { Length: > 0 } script)
            {
                text.AppendLine("    The script:");
                Indented(text, script, "      ");
            }
            return;
        }

        string called = $"{turn.ToolName} {turn.ArgumentsJson.Trim()}".Trim();

        if (turn.Decision == false)
        {
            text.AppendLine($"  Asked to call {called} — declined.");
            return;
        }
        if (turn.Decision is null || turn.Result is null)
        {
            text.AppendLine($"  Called {called} — it did not finish.");
            return;
        }

        text.AppendLine(turn.ResultIsError ? $"  Called {called} — it came back as an error:" : $"  Called {called} — it answered:");
        Indented(text, turn.Result, "    ");
        if (turn.Png is not null) text.AppendLine("    (and a picture)");
    }

    private static void Indented(StringBuilder text, string lines, string indent)
    {
        foreach (string line in lines.Trim().Split('\n')) text.AppendLine(indent + line.TrimEnd('\r'));
    }

    private static void AppendFiles(StringBuilder text, string said, IReadOnlyList<AiAttachment> files)
    {
        if (files.Count == 0) return;

        IEnumerable<string> named = files.Select(file => file.Excluded
            ? $"{file.Name} ({AiAttachment.Size(file.Data.Length)}, not sent)"
            : $"{file.Name} ({AiAttachment.Size(file.Data.Length)})");
        text.AppendLine($"  {said}: {string.Join(", ", named)}");
    }

    /// <summary>
    /// What a proposal was, in words — "a script of 3 lines for gen", "a sequence script of 12 lines" —
    /// from the script it put forward, or the one it was asked to when it never answered, and the
    /// arguments it was called with.
    /// </summary>
    private static string ProposalSaid(ChatTurn turn)
    {
        JsonObject? arguments = ArgumentsOf(turn.ArgumentsJson);
        int lines = (ScriptOf(turn) ?? "").Split('\n').Count(line => line.Trim().Length > 0);
        string count = lines == 1 ? "1 line" : $"{lines} lines";

        if (string.Equals(TextIn(arguments, "kind"), "sequence", StringComparison.OrdinalIgnoreCase))
            return $"a sequence script of {count}";

        return TextIn(arguments, "instrument") is { Length: > 0 } instrument
            ? $"a script of {count} for {instrument}"
            : $"a script of {count}";
    }

    /// <summary>The script a proposal put forward, or the one in its arguments where it never answered.</summary>
    private static string? ScriptOf(ChatTurn turn)
        => (turn.Proposal ?? TextIn(ArgumentsOf(turn.ArgumentsJson), "script"))?.Replace("\r\n", "\n").Trim('\n');

    /// <summary>A call's arguments as the object they were meant as — read through a string of JSON, as the tools read them — or null.</summary>
    private static JsonObject? ArgumentsOf(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);
            if (node is JsonValue value && value.TryGetValue(out string? inner)) node = JsonNode.Parse(inner);
            return node as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TextIn(JsonObject? arguments, string name)
        => arguments?[name] is JsonValue value && value.TryGetValue(out string? text) ? text.Trim() : null;

    private static string Shortened(string text)
    {
        string flat = text.Trim();
        return flat.Length <= MostReplayed ? flat : flat[..MostReplayed] + "...";
    }

    /// <summary>What a stop or a failure adds when the run had already sent something: nothing on a bench is taken back.</summary>
    private static string SentStays(bool sentAnything)
        => sentAnything ? " Anything it sent to an instrument before that stays sent — there is no undo on hardware." : "";
}
