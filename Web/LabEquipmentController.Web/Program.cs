// Core's types live in the LabEquipmentController namespace. The classes under Bench/ pick
// that up implicitly by being nested inside it; this file has no namespace of its own, so
// it has to say so.
using LabEquipmentController;
using LabEquipmentController.Web.Bench;
using LabEquipmentController.Web.Client.Contracts;
using LabEquipmentController.Web.Hubs;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<BenchService>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton<IAiClient, AiClient>();
builder.Services.AddSingleton(sp =>
{
    var options = new AiOptions();
    sp.GetRequiredService<IConfiguration>().GetSection(AiOptions.Section).Bind(options);
    return options;
});
builder.Services.AddSingleton<AiService>();

var app = builder.Build();

// Debugging the browser half is a development-only affair, and calling this in production
// throws.
if (app.Environment.IsDevelopment()) app.UseWebAssemblyDebugging();

// Order matters and is easy to get subtly wrong: the framework files (the .wasm runtime and
// the assemblies) are served first, then everything else in the client's wwwroot.
//
// Outside Development these assets only exist after `dotnet publish` copies them into
// wwwroot — a plain `dotnet run` with no environment set defaults to Production, serves
// nothing, and answers the API perfectly while every page 404s. launchSettings.json pins
// Development for exactly that reason.
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();

// ------------------------------------------------------------------- the API
//
// Minimal APIs rather than controllers: every endpoint here is a thin translation between
// a DTO and a call into Core or BenchService, and a controller class per group would be
// three times the code for the same three lines of work.

var api = app.MapGroup("/api");

api.MapGet("/interfaces", (BenchService bench) => bench.Interfaces());

api.MapPost("/scan", async (ScanRequest req, BenchService bench, CancellationToken ct)
    => await bench.ScanAsync(req, null, ct));

api.MapGet("/sessions", (BenchService bench) => bench.Sessions());

api.MapPost("/sessions", async (ConnectRequest req, BenchService bench, CancellationToken ct) =>
{
    try { return Results.Ok(await bench.ConnectAsync(req, ct)); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway); }
});

api.MapDelete("/sessions/{id}", async (string id, BenchService bench)
    => await bench.DisconnectAsync(id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/sessions/{id}/command", async (string id, CommandRequest req, BenchService bench, CancellationToken ct)
    => await bench.SendAsync(id, req.Text, ct));

api.MapPost("/sessions/{id}/waveform", async (string id, int? channel, BenchService bench, CancellationToken ct)
    => await bench.WaveformAsync(id, channel ?? 1, ct));

api.MapPost("/sessions/{id}/screenshot", async (string id, BenchService bench, CancellationToken ct)
    => await bench.ScreenshotAsync(id, ct));

// ---------------------------------------------------------------- catalogs

api.MapGet("/catalogs", () =>
    Enum.GetValues<InstrumentFamily>()
        .Select(f => (Family: f, Reference: CommandReference.ForFamily(f)))
        .Where(x => x.Reference is not null)
        .Select(x => new CatalogSummary(
            x.Family.ToString(), x.Reference!.Instrument, x.Reference.Manufacturer,
            x.Reference.Commands.Count, x.Reference.Commands.Count(c => c.BenchVerified),
            x.Reference.Guide?.Title, x.Reference.Guide?.Url))
        .OrderBy(c => c.Manufacturer).ThenBy(c => c.Instrument)
        .ToList());

api.MapGet("/catalogs/{family}", (string family, string? filter, int? limit) =>
{
    if (!Enum.TryParse<InstrumentFamily>(family, ignoreCase: true, out var f))
        return Results.NotFound();
    var reference = CommandReference.ForFamily(f);
    if (reference is null) return Results.NotFound();

    IEnumerable<CommandRef> commands = reference.Commands;
    if (filter is { Length: > 0 })
        // The same three-way match the CLI uses: syntax, description, or the command as it
        // would actually be sent — a guide prints "[SENSe:]VOLTage[:DC]:NPLC" and nobody
        // types the brackets.
        commands = commands.Where(c =>
            c.Syntax.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || c.Description.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || ScpiSyntax.Matches(filter, c.Syntax));

    return Results.Ok(commands.Take(limit ?? 500).Select(AiService.Map).ToList());
});

// ----------------------------------------------------------------- scripts

api.MapGet("/examples/script/{family}", (string family) =>
    Enum.TryParse<InstrumentFamily>(family, ignoreCase: true, out var f)
        ? ScriptExamples.ForFamily(f).Select(e => new ExampleDto(e.Name, e.Script)).ToList()
        : []);

api.MapGet("/examples/sequence", () =>
    SequenceExamples.All.Select(e => new ExampleDto(e.Name, e.Script)).ToList());

api.MapPost("/runs/script", (ScriptRunRequest req, RunService runs) => runs.StartScript(req));

api.MapPost("/runs/sequence", (SequenceRunRequest req, RunService runs) => runs.StartSequence(req));

api.MapPost("/runs/{runId}/stop", (string runId, RunService runs)
    => runs.Stop(runId) ? Results.NoContent() : Results.NotFound());

api.MapPost("/sequence/requirements", (SequenceRunRequest req)
    => SequenceRunner.Requirements(req.Script)
        .Select(r => new SequenceRequirement(r.Alias, r.Model)).ToList());

// ---------------------------------------------------------------------- AI

api.MapGet("/ai", (AiService ai) => ai.Status());

api.MapPost("/ai/script", async (AiScriptRequest req, AiService ai, CancellationToken ct)
    => await ai.WriteScriptAsync(req, ct));

api.MapPost("/ai/extract", async (AiExtractRequest req, AiService ai, CancellationToken ct)
    => await ai.ExtractAsync(req, ct));

app.MapHub<BenchHub>("/hub/bench");

app.MapFallbackToFile("index.html");

// Instruments are physical things; leaving one in remote mode with its front panel dead is
// the rudest way to shut down. Hand them all back on the way out.
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<BenchService>().DisposeAsync().AsTask().GetAwaiter().GetResult());

app.Run();

namespace LabEquipmentController.Web
{
    /// <summary>
    /// A handle on this assembly for <c>WebApplicationFactory</c>, which only needs a type
    /// to find the entry point from.
    /// </summary>
    /// <remarks>
    /// Not the usual <c>public partial class Program</c>: top-level statements put that in
    /// the global namespace, and the CLI has one too. With both projects referenced by the
    /// test assembly the name is ambiguous and nothing compiles. A named type in a
    /// namespace of its own cannot collide.
    /// </remarks>
    public sealed class WebEntryPoint;
}
