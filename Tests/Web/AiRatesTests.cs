using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// A connection's own rates, as the web's settings box sends them: kept with the connection,
/// written to <c>ai.json</c> and read back, left alone by a page that does not send them, and
/// handed to the page with the connection so its assistant can price an answer.
/// </summary>
public sealed class AiRatesTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "lec-rates-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_data, recursive: true); } catch { /* a temp folder is not worth a failure */ }
    }

    private AiSettingsStore Store() => new(
        new AiOptions(),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = _data }).Build(),
        NullLogger<AiSettingsStore>.Instance);

    private static AiSettingsUpdate Update(string id, AiRates? rates) => new(
        "Anthropic", "", "claude-sonnet-5", null, 300, null, Id: id, Rates: rates);

    [Fact]
    public void Rates_typed_in_the_box_are_kept_and_survive_a_restart()
    {
        var store = Store();
        string id = store.Book.Items[0].Id;

        Assert.Null(store.Apply(Update(id, new AiRates(2.5m, null, 12m))));

        AiConnection again = Store().Book.Find(id)!;
        Assert.Equal(2.5m, again.InputPrice);
        Assert.Null(again.CachedPrice);
        Assert.Equal(12m, again.OutputPrice);
    }

    [Fact]
    public void A_page_that_sends_no_rates_leaves_the_stored_ones_alone()
    {
        var store = Store();
        string id = store.Book.Items[0].Id;
        store.Apply(Update(id, new AiRates(2.5m, 0.25m, 12m)));

        store.Apply(Update(id, null));

        AiConnection kept = store.Book.Find(id)!;
        Assert.Equal((2.5m, 0.25m, 12m), (kept.InputPrice!.Value, kept.CachedPrice!.Value, kept.OutputPrice!.Value));
    }

    [Fact]
    public void A_negative_rate_is_taken_for_blank()
    {
        var store = Store();
        string id = store.Book.Items[0].Id;

        store.Apply(Update(id, new AiRates(-1m, null, 3m)));

        Assert.Null(store.Book.Find(id)!.InputPrice);
    }
}
