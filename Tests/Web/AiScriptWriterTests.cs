using System.Text.Json.Nodes;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabEquipmentController.Tests;

/// <summary>
/// The web's script writer, as far as the prompt: what the model is told about the bench, and
/// whether what it is told to write then binds. A fake provider stands where the real one would
/// and keeps what it was sent.
/// </summary>
/// <remarks>
/// The web used to describe the bench itself, and got three things wrong at once: the identity
/// went where the model goes and the address where the identity goes; every meter was
/// "multimet", so two of them threw in the check of the reply, after the model had been paid
/// for; and a single-instrument script was given an alias its lines must not carry. It is Core's
/// ScriptContext now, as the desktop's is.
/// </remarks>
public class AiScriptWriterTests : IAsyncLifetime
{
    private const string Generator = "Siglent Technologies,SDG2042X,SDG2XCAD1R0001,2.01.01.35R3B2";
    private const string MeterA = "Siglent Technologies,SDM3065X,SDM36HCD801207,3.02.01.13";
    private const string MeterB = "Siglent Technologies,SDM3065X,SDM36HCD801208,3.02.01.13";

    private readonly string _data = Path.Combine(Path.GetTempPath(), "lec-ai-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProvider _provider = new();
    private readonly BenchService _bench;
    private readonly AiService _ai;

    public AiScriptWriterTests()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSignalR();
        var hub = services.BuildServiceProvider().GetRequiredService<IHubContext<BenchHub>>();
        _bench = new BenchService(NullLogger<BenchService>.Instance, hub);

        var settings = new AiSettingsStore(
            new AiOptions { ApiKey = "sk-test" },
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["LEC_DATA"] = _data })
                .Build(),
            NullLogger<AiSettingsStore>.Instance);
        _ai = new AiService(settings, _bench, _provider, NullLogger<AiService>.Instance);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _bench.DisposeAsync();
        try { Directory.Delete(_data, recursive: true); } catch { /* a temp folder is not worth a failure */ }
    }

    [Fact]
    public async Task Two_meters_of_one_model_are_two_names_each_declared_by_serial_number()
    {
        string gen = await Connect("192.168.1.5", Generator);
        string a = await Connect("192.168.1.7", MeterA);
        string b = await Connect("192.168.1.8", MeterB);
        _provider.Reply = """{"script": "DEVICE dmm : SDM36HCD801207\nDEVICE dmm2 : SDM36HCD801208\ndmm: MEASure:VOLTage:DC?\ndmm2: MEASure:VOLTage:DC?"}""";

        var reply = await _ai.WriteScriptAsync(
            new AiScriptRequest("read both meters", [b, gen, a], IsSequence: true, null, null), default);

        Assert.Null(reply.Error);   // two meters used to be one alias, and this threw
        Assert.Contains("### gen — SDG2042X", _provider.Sent);
        Assert.Contains("### dmm — SDM36HCD801207", _provider.Sent);
        Assert.Contains("### dmm2 — SDM36HCD801208", _provider.Sent);
        Assert.Contains("*IDN? — " + MeterA, _provider.Sent);          // the identity where it goes
        Assert.DoesNotContain("*IDN? — 192.168", _provider.Sent);       // and not the address
        Assert.DoesNotContain("### multimet", _provider.Sent);          // the old alias, for both

        // Written as it was told to write them, the DEVICE lines bind, each to its own meter.
        var rows = _bench.BindSequence(Declared(_provider.Sent!), new Dictionary<string, string>());
        Assert.Equal([gen, a, b], rows.Select(r => r.SessionId));
    }

    /// <summary>
    /// The ticks say what the model is told about, not what is on the bench. A meter left
    /// unticked is still a second meter of that model, so the ticked one is declared by serial
    /// number — named by model, the line would bind neither.
    /// </summary>
    [Fact]
    public async Task A_meter_left_unticked_still_counts_as_a_second_of_its_model()
    {
        await Connect("192.168.1.7", MeterA);
        string b = await Connect("192.168.1.8", MeterB);

        await _ai.WriteScriptAsync(new AiScriptRequest("read it", [b], IsSequence: true, null, null), default);

        Assert.Contains("Declare it as: DEVICE dmm : SDM36HCD801208", _provider.Sent);
        Assert.DoesNotContain("SDM36HCD801207", _provider.Sent);   // the unticked one is not described
    }

    [Fact]
    public async Task The_names_of_the_script_being_revised_are_kept()
    {
        string a = await Connect("192.168.1.7", MeterA);
        string b = await Connect("192.168.1.8", MeterB);

        await _ai.WriteScriptAsync(new AiScriptRequest(
            "make it faster", [a, b], IsSequence: true,
            "DEVICE left : SDM36HCD801208\nDEVICE right : SDM3065X\nleft: MEASure:VOLTage:DC?", null), default);

        Assert.Contains("Declare it as: DEVICE left : SDM36HCD801208", _provider.Sent);
        Assert.Contains("Declare it as: DEVICE right : SDM3065X", _provider.Sent);
    }

    /// <summary>
    /// And when it is not being revised. SequenceForm reads the names from its editor whatever
    /// "Revise the current script" says; here they were kept only when it was ticked, so asking for
    /// a new script beside a working one renamed every device in it. The script itself still does
    /// not go to the model.
    /// </summary>
    [Fact]
    public async Task The_names_in_the_editor_are_kept_when_it_is_not_being_revised()
    {
        string a = await Connect("192.168.1.7", MeterA);
        string b = await Connect("192.168.1.8", MeterB);

        await _ai.WriteScriptAsync(new AiScriptRequest(
            "read both, fresh", [a, b], IsSequence: true, CurrentScript: null, RecentOutput: null,
            EditorScript: "DEVICE left : SDM36HCD801208\nDEVICE right : SDM3065X\nleft: MEASure:VOLTage:DC?"), default);

        Assert.Contains("Declare it as: DEVICE left : SDM36HCD801208", _provider.Sent);
        Assert.Contains("Declare it as: DEVICE right : SDM3065X", _provider.Sent);
        Assert.DoesNotContain("left: MEASure:VOLTage:DC?", _provider.Sent);
    }

    /// <summary>
    /// The binding table's picks reach the writer, so the part it is told a meter plays is the part
    /// the table shows. Two meters asked for by model bind neither by the rule, and the rule on its
    /// own would have named them the other way round.
    /// </summary>
    [Fact]
    public async Task The_parts_picked_in_the_table_are_what_the_writer_is_told()
    {
        string a = await Connect("192.168.1.7", MeterA);
        string b = await Connect("192.168.1.8", MeterB);
        const string script = "DEVICE left : SDM3065X\nDEVICE right : SDM3065X";

        await _ai.WriteScriptAsync(new AiScriptRequest(
            "read both", [a, b], IsSequence: true, CurrentScript: null, RecentOutput: null,
            EditorScript: script, Picks: new Dictionary<string, string> { ["left"] = b, ["right"] = a }), default);

        Assert.Contains("Declare it as: DEVICE left : SDM36HCD801208", _provider.Sent);
        Assert.Contains("Declare it as: DEVICE right : SDM36HCD801207", _provider.Sent);

        // And what the table shows for the same picks is the same answer.
        var rows = _bench.BindSequence(script, new Dictionary<string, string> { ["left"] = b, ["right"] = a });
        Assert.Equal([b, a], rows.Select(r => r.SessionId));
    }

    /// <summary>One instrument, so no alias — its lines carry no prefix — and no DEVICE line.</summary>
    [Fact]
    public async Task A_single_instrument_script_is_told_its_model_and_given_no_alias()
    {
        string a = await Connect("192.168.1.7", MeterA);

        var reply = await _ai.WriteScriptAsync(
            new AiScriptRequest("read the voltage", [a], IsSequence: false, null, null), default);

        Assert.Null(reply.Error);
        var lines = _provider.Sent!.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("### SDM3065X", lines);
        Assert.Contains("*IDN? — " + MeterA, lines);
        Assert.DoesNotContain(lines, l => l.StartsWith("Declare it as", StringComparison.Ordinal));
    }

    private async Task<string> Connect(string address, string identity)
    {
        var instrument = new FakeInstrumentClient();
        instrument.Responses["*IDN?"] = identity;
        return (await _bench.OpenAsync(BenchService.ParseAddress(address), () => instrument, default)).Id;
    }

    /// <summary>The DEVICE lines the model was told to write, as a script.</summary>
    private static string Declared(string payload)
        => string.Join("\n", payload.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => l.StartsWith("Declare it as: ", StringComparison.Ordinal))
            .Select(l => l["Declare it as: ".Length..]));

    /// <summary>A provider that answers what it is given to answer, and keeps what it was sent.</summary>
    private sealed class FakeProvider : IAiClient
    {
        public string Reply { get; set; } = """{"script": "*IDN?"}""";

        public string? Sent { get; private set; }

        public Task<string> CompleteAsync(AiConnection connection, string apiKey, string instruction,
                                          AiPayload payload, JsonNode schema, CancellationToken ct = default)
        {
            Sent = payload.Text;
            return Task.FromResult(Reply);
        }
    }
}
