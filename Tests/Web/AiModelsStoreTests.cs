using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// The models the page's AI settings edit: a key kept to the transport it was given for, a copy
/// that brings its key and sits after its original, and the cap on an answer.
/// </summary>
public sealed class AiModelsStoreTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "lec-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { /* a temp folder is not worth a failure */ }
    }

    private AiSettingsStore Store(AiOptions? configured = null) => new(
        configured ?? new AiOptions(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = _data }).Build(),
        NullLogger<AiSettingsStore>.Instance);

    private static AiSettingsUpdate Update(string id, string endpoint, string? key, string provider = "OpenAiCompatible")
        => new(provider, endpoint, "some-model", null, 300, key, null, id, "");

    [Fact]
    public void A_key_saved_for_https_is_let_go_when_the_address_becomes_http_with_nothing_typed()
    {
        AiSettingsStore store = Store();
        string id = store.Book.Items[0].Id;
        Assert.Null(store.Apply(Update(id, "https://gateway.example", "sk-secret")));

        Assert.Null(store.Apply(Update(id, "http://gateway.example", key: null)));

        Assert.Equal("", store.KeyFor(id));
    }

    /// <summary>Somebody typing one is the person's own entry, and is left alone.</summary>
    [Fact]
    public void A_key_typed_along_with_an_http_address_is_kept()
    {
        AiSettingsStore store = Store();
        string id = store.Book.Items[0].Id;
        Assert.Null(store.Apply(Update(id, "https://gateway.example", "sk-secret")));

        Assert.Null(store.Apply(Update(id, "http://gateway.example", "sk-typed-again")));

        Assert.Equal("sk-typed-again", store.KeyFor(id));
    }

    /// <summary>A model saved without encryption keeps its key, since somebody chose that.</summary>
    [Fact]
    public void A_key_saved_for_http_stays_when_it_is_saved_again()
    {
        AiSettingsStore store = Store();
        string id = store.Book.Items[0].Id;
        Assert.Null(store.Apply(Update(id, "http://localhost:11434", "sk-local")));

        Assert.Null(store.Apply(Update(id, "http://localhost:11434", key: null)));

        Assert.Equal("sk-local", store.KeyFor(id));
    }

    [Fact]
    public void A_copy_brings_its_key_sits_right_after_its_original_and_is_picked()
    {
        AiSettingsStore store = Store();
        string first = store.Book.Items[0].Id;
        Assert.Null(store.Apply(Update(first, "https://api.openai.com", "sk-first")));
        string last = store.Add("Gemini");

        string? copy = store.Duplicate(first);

        Assert.NotNull(copy);
        AiConnections book = store.Book;
        Assert.Equal([first, copy, last], book.Items.Select(c => c.Id));
        Assert.Equal(copy, book.SelectedId);
        Assert.Equal("sk-first", store.KeyFor(copy));
        Assert.Equal("some-model (copy)", book.Find(copy)!.Name);
    }

    [Fact]
    public void A_copy_of_a_copy_is_numbered_and_of_nothing_is_nothing()
    {
        AiSettingsStore store = Store();
        string first = store.Book.Items[0].Id;
        store.Apply(Update(first, "", key: null) with { Name = "Claude", Provider = "Anthropic" });

        store.Duplicate(first);
        string? second = store.Duplicate(first);

        Assert.Equal("Claude (copy 2)", store.Book.Find(second)!.Name);
        Assert.Null(store.Duplicate("no-such-id"));
    }

    [Fact]
    public void The_cap_on_an_answer_is_kept_and_zero_puts_back_the_default()
    {
        AiSettingsStore store = Store();
        string id = store.Book.Items[0].Id;

        store.Apply(Update(id, "", null, "Anthropic") with { MaxTokens = 16000 });
        Assert.Equal(16000, Store().Book.Find(id)!.MaxTokens);

        store.Apply(Update(id, "", null, "Anthropic"));
        Assert.Equal(16000, store.Book.Find(id)!.MaxTokens);

        store.Apply(Update(id, "", null, "Anthropic") with { MaxTokens = 0 });
        Assert.Null(store.Book.Find(id)!.MaxTokens);
    }

    /// <summary>An id the server does not have adds the model, which is how the page's New saves in one step.</summary>
    [Fact]
    public void Saving_under_an_unknown_id_adds_and_picks_it_and_a_refusal_keeps_nothing()
    {
        AiSettingsStore store = Store();
        int before = store.Book.Items.Count;

        Assert.NotNull(store.Apply(new AiSettingsUpdate("OpenAiCompatible", "not an address", "m", null, 300, "k", null, "new-one", "Bad")));
        Assert.Equal(before, store.Book.Items.Count);

        Assert.Null(store.Apply(new AiSettingsUpdate("Anthropic", "", "", null, 300, "sk-new", null, "new-one", "Claude")));
        Assert.Equal(before + 1, store.Book.Items.Count);
        Assert.Equal("Claude", store.Book.Selected!.Name);
        Assert.Equal("sk-new", store.ApiKey);
    }
}
