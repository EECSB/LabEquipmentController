using System.Globalization;
using Microsoft.JSInterop;

namespace LabEquipmentController.Web.Client.Services;

/// <summary>
/// What the person chose under AI settings that is theirs rather than the bench's, kept in this
/// browser: how the assistant's microphone turns speech into words, and how many rounds of tool
/// calls it may make before it asks to go on. One instance, so the settings window and the chat
/// hear each other's change.
///
/// **One read, shared by everybody who asks before it answers.** AI settings and the chat can both
/// ask as a page comes up; marking the setting loaded before storage had answered told the second
/// reader the default (the AI guide's pitfall). A choice made while the read is out is newer, and
/// kept over it.
/// </summary>
public sealed class AiPreferences(IJSRuntime js)
{
    private const string SpeechKey = "lec.speechInput";
    private const string RoundsKey = "lec.aiToolRounds";

    /// <summary>The fewest a person may set: one round is a single look or change, and anything less is no tools at all.</summary>
    public const int FewestRounds = 1;

    /// <summary>The most: a cap on a cap, so a slip of the keyboard cannot hand a run unwatched spend without end.</summary>
    public const int MostRounds = 1000;

    private bool _speechLoaded;
    private Task<SpeechMode>? _speechLoading;
    private SpeechMode _speech = SpeechMode.Browser;

    private bool _roundsLoaded;
    private Task<int>? _roundsLoading;
    private int _rounds = LlmProviders.DefaultToolRounds;

    /// <summary>Raised after the speech mode changes, so the chat's microphone says what it does now.</summary>
    public event Func<Task>? Changed;

    public Task<SpeechMode> SpeechAsync()
    {
        if (_speechLoaded) return Task.FromResult(_speech);
        return _speechLoading ??= LoadSpeechAsync();
    }

    private async Task<SpeechMode> LoadSpeechAsync()
    {
        string? text;
        try
        {
            text = await js.InvokeAsync<string?>("lec.assistant.stored", SpeechKey);
        }
        catch
        {
            // A read that failed is not kept as the answer: the next ask reads again.
            _speechLoading = null;
            throw;
        }

        if (!_speechLoaded)
        {
            _speechLoaded = true;
            _speech = text == nameof(SpeechMode.Provider) ? SpeechMode.Provider : SpeechMode.Browser;
        }
        return _speech;
    }

    public async Task SetSpeechAsync(SpeechMode chosen)
    {
        _speechLoaded = true;
        if (chosen == _speech) return;

        _speech = chosen;
        await js.InvokeVoidAsync("lec.assistant.keep", SpeechKey, chosen.ToString());

        if (Changed is null) return;
        foreach (Delegate listener in Changed.GetInvocationList())
            await ((Func<Task>)listener)();
    }

    public Task<int> ToolRoundsAsync()
    {
        if (_roundsLoaded) return Task.FromResult(_rounds);
        return _roundsLoading ??= LoadRoundsAsync();
    }

    private async Task<int> LoadRoundsAsync()
    {
        string? text;
        try
        {
            text = await js.InvokeAsync<string?>("lec.assistant.stored", RoundsKey);
        }
        catch
        {
            _roundsLoading = null;
            throw;
        }

        if (!_roundsLoaded)
        {
            _roundsLoaded = true;
            _rounds = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int kept)
                ? Math.Clamp(kept, FewestRounds, MostRounds)
                : LlmProviders.DefaultToolRounds;
        }
        return _rounds;
    }

    /// <summary>Keeps a new number, held between <see cref="FewestRounds"/> and <see cref="MostRounds"/>, and says what was kept.</summary>
    public async Task<int> SetToolRoundsAsync(int chosen)
    {
        _roundsLoaded = true;
        chosen = Math.Clamp(chosen, FewestRounds, MostRounds);
        if (chosen == _rounds) return _rounds;

        _rounds = chosen;
        await js.InvokeVoidAsync("lec.assistant.keep", RoundsKey, chosen.ToString(CultureInfo.InvariantCulture));
        return _rounds;
    }
}
