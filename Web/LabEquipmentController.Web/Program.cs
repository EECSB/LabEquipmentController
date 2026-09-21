// Core's types live in the LabEquipmentController namespace. The classes under Bench/ pick
// that up implicitly by being nested inside it; this file has no namespace of its own, so
// it has to say so.
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.StaticFiles;
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
// Configuration is the bottom layer; the store lays anything set in the settings box on top
// of it and is what the rest of the app reads.
builder.Services.AddSingleton<AiSettingsStore>();
builder.Services.AddSingleton<AiService>();
builder.Services.AddSingleton<DatasheetService>();
// The server as a host's bench (LEC_SERVICE_TOKEN), and below a path (LEC_PATH_BASE). Off, it is
// the standalone app exactly as before; see ServiceMode.
builder.Services.AddSingleton<ServiceMode>();

var app = builder.Build();

// Debugging the browser half is a development-only affair, and calling this in production
// throws.
if (app.Environment.IsDevelopment()) app.UseWebAssemblyDebugging();

// ------------------------------------------------------------ service mode
//
// Below a path when a host proxies this server at one: UsePathBase strips the prefix before
// anything routes, so every route below is written as it always was, and the page shell is
// served with a <base href> that says where it lives (see the fallback further down).
//
// And a bearer token on the API and the hub when one is configured. The page itself stays
// open, because it is the host's frame that loads it and a page can do nothing without the API;
// the token is checked here rather than by an authentication scheme because there is exactly
// one caller with exactly one token, and a scheme would be an identity system for a bench.
var service = app.Services.GetRequiredService<ServiceMode>();
if (service.PathBase.Length > 0) app.UsePathBase(service.PathBase);

// Routing runs HERE, and saying so is the whole point of this line. Without an explicit
// UseRouting, WebApplication inserts one at the very START of the pipeline — before the
// UsePathBase above — so the endpoint is chosen from the path as it arrived, /base/api/…, which
// no API route matches and the page-shell fallback does. The bench then answers every authorized
// API call with its own index.html: the token is still checked correctly, the page still loads,
// and nothing else works at all. Found on 2026-09-20 by Treeality's Instruments plugin, the first
// caller to serve this server below a path.
app.UseRouting();

app.Use(async (context, next) =>
{
    if (service.Enabled
        && (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/hub"))
        && !service.Admits(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await context.Response.WriteAsync(ServiceMode.Challenge);
        return;
    }
    await next();
});

// Order matters and is easy to get subtly wrong: the framework files (the .wasm runtime and
// the assemblies) are served first, then everything else in the client's wwwroot.
//
// Outside Development these assets only exist after `dotnet publish` copies them into
// wwwroot — a plain `dotnet run` with no environment set defaults to Production, serves
// nothing, and answers the API perfectly while every page 404s. launchSettings.json pins
// Development for exactly that reason.
app.UseBlazorFrameworkFiles();

// index.html and app.css are served under their own names, unlike everything under
// _framework/ which carries a content hash and can be cached forever. A browser that has
// been to this server before will happily keep showing the previous build's stylesheet and
// page shell — which looks exactly like an update that did not take, and is not something
// anyone will think to blame the cache for.
//
// no-cache is not no-store: the file is still cached, the browser just asks whether it has
// changed first, and an unchanged one comes back as a 304 with no body. One conditional
// request per file per load, in exchange for never serving yesterday's UI.
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = Revalidate });

// The page shell, served by hand rather than as the file it is, because its one absolute
// reference — <base href> — has to say where the app is served from: "/" alone, or the path a
// host proxies it at (LEC_PATH_BASE). Everything the page then asks for is relative to that
// line, so the one line is what makes a sub-path work. Same no-cache as the file had.
app.MapFallback(async context =>
{
    var shell = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!shell.Exists)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    string html;
    using (var reader = new StreamReader(shell.CreateReadStream()))
        html = await reader.ReadToEndAsync();

    if (service.PathBase.Length > 0)
        html = html.Replace("<base href=\"/\" />", $"<base href=\"{service.PathBase}/\" />", StringComparison.Ordinal);

    context.Response.ContentType = "text/html; charset=utf-8";
    context.Response.Headers.CacheControl = "no-cache";
    await context.Response.WriteAsync(html);
});

static void Revalidate(StaticFileResponseContext ctx)
    => ctx.Context.Response.Headers.CacheControl = "no-cache";

// ------------------------------------------------------------------- the API
//
// Minimal APIs rather than controllers: every endpoint here is a thin translation between
// a DTO and a call into Core or BenchService, and a controller class per group would be
// three times the code for the same three lines of work.

var api = app.MapGroup("/api");

api.MapGet("/interfaces", (BenchService bench) => bench.Interfaces());

// The server's serial ports. Deliberately a sibling of /interfaces rather than of /scan:
// this lists and never opens, which is what arriving at the card gives you. Opening them is
// /serial/scan below, and only because it was asked to (SPEC §4). They are the *server's*
// ports — the browser has none, and the address box says so.
api.MapGet("/ports", () => SerialPorts.Names());

// The same ports as rows for the results table — port name, default line settings, no
// identity, because nothing has been asked yet. Built by Core so the "9600-8-N-1" the page
// shows is the one SerialSettings.Default actually means, rather than a string that agreed
// with it on the day it was typed.
api.MapGet("/serial/list", (BenchService bench) => bench.SerialPortRows());

// The whole sweep in one answer, for anything that is not the browser — the page uses the
// hub's Scan instead, which reports as it goes rather than only at the end.
api.MapPost("/scan", async (ScanRequest req, BenchService bench, CancellationToken ct)
    => await bench.ScanAsync(req, null, ct));

// The serial sweep in one answer, the same way. /ports above lists and never opens; this
// opens, and only because it was asked to.
api.MapPost("/serial/scan", async (SerialScanRequest req, BenchService bench, CancellationToken ct)
    => await bench.ScanSerialAsync(req, null, ct));

// Export Results: the same CSV the desktop's button writes, from the same writer in Core, so
// the two builds cannot produce different files from the same bench.
//
// The list is posted back up rather than read from the server, because a scan streams its
// results out as it finds them and nothing here keeps the last one. The browser has the only
// copy, which is also the honest one — it exports the list you are looking at.
//
// Which of the two headers it gets is decided by the rows themselves: a serial row carries
// line settings in the second column and a network one carries a port number, and a file
// headed "IP Address" over a column of COM3 would be a worse lie than a second header.
api.MapPost("/scan/export", (List<DeviceDto> devices)
    => Results.Text(
        devices.Any(d => d.Settings.Length > 0)
            ? ScanResultExport.ToCsv(ScanResultExport.SerialColumns,
                                     devices.Select(d => (d.Address, d.Settings, d.Transport, d.Identity)))
            : ScanResultExport.ToCsv(devices.Select(d => (d.Address, d.Port, d.Transport, d.Identity))),
        "text/csv"));

// The plot's arithmetic, which is Core's. See PlotService for why the browser asks rather
// than working it out itself.
api.MapPost("/plot", (PlotRequest req) => PlotService.Build(req));

// A document of the app has loaded. Whether the bench it is opening onto is still wanted is
// decided in there — see BenchService.PageOpenedAsync. Answered before the runtime is started,
// so a page that is opening onto a bench nobody wants any more draws an empty one rather than
// drawing the last window’s consoles and taking them away again.
api.MapPost("/bench/opened", async (bool? resumed, BenchService bench) =>
{
    await bench.PageOpenedAsync(resumed == true);
    return Results.NoContent();
});

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

api.MapPost("/sessions/{id}/discover", async (string id, BenchService bench, CancellationToken ct)
    => await bench.DiscoverAsync(id, ct));

// "1,2" — several channels in one capture, so they can be read against each other. Anything
// missing or unreadable means channel 1, which is what a scope shows first.
api.MapPost("/sessions/{id}/waveform", async (string id, string? channels, BenchService bench, CancellationToken ct)
    => await bench.WaveformAsync(id, ChannelList(channels), ct));

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

// ------------------------------------------------------------------- about

// The About box's facts, counted here rather than written down anywhere. The version comes
// off Core because Core is the half that does the work and the half that ships as a package;
// the server assembly carries no version of its own to quote.
api.MapGet("/about", () =>
{
    Assembly core = typeof(CommandReference).Assembly;
    string version = core.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                     ?? core.GetName().Version?.ToString(3) ?? "—";
    // The SDK appends "+<commit sha>" to the informational version. True, and not something
    // an About box should read out.
    int plus = version.IndexOf('+');
    if (plus > 0) version = version[..plus];

    int catalogs = 0, commands = 0, verified = 0;
    foreach (InstrumentFamily f in Enum.GetValues<InstrumentFamily>())
    {
        CommandReference? r = CommandReference.ForFamily(f);
        if (r is null || r.Commands.Count == 0) continue;
        catalogs++;
        commands += r.Commands.Count;
        verified += r.Commands.Count(c => c.BenchVerified);
    }

    // Off the assembly, not written here: the same two figures the desktop About box states and
    // the NuGet listing carries, from the one place they are declared. See AppInfo.
    return new AboutDto(version, RuntimeInformation.FrameworkDescription, RuntimeInformation.OSDescription,
                        catalogs, commands, verified,
                        AppInfo.Repository, AppInfo.License);
});

// ----------------------------------------------------------------- datasheets

// The user's own copies of the programming guides. The app ships none of them — they are the
// vendors' copyright — so on the desktop they are a folder on the user's machine and here they
// are a folder on the server's, put there by uploading. See DatasheetService.
api.MapGet("/datasheets", (DatasheetService sheets) => sheets.List());

// Which guide goes with a catalog, by Core's own matching, so the desktop and this agree.
api.MapGet("/datasheets/for/{family}", (string family, DatasheetService sheets)
    => Enum.TryParse(family, ignoreCase: true, out InstrumentFamily f) && sheets.For(f) is { } found
        ? Results.Ok(found)
        : Results.NoContent());

// The bytes. Inline rather than as a download: the point is to read it beside the commands it
// documents, which is what the desktop's embedded viewer is for.
api.MapGet("/datasheets/file/{**path}", (string path, DatasheetService sheets)
    => sheets.Locate(path) is { } full
        ? Results.File(full, "application/pdf", enableRangeProcessing: true)
        : Results.NotFound());

api.MapPost("/datasheets", async (DatasheetUpload req, DatasheetService sheets, CancellationToken ct)
    => await sheets.SaveAsync(req.Manufacturer, req.FileName, Convert.FromBase64String(req.Base64), ct)
           is { } saved
        ? Results.Ok(saved)
        : Results.BadRequest("That file could not be saved. It has to be a PDF."));

api.MapDelete("/datasheets/{**path}", (string path, DatasheetService sheets)
    => sheets.Delete(path) ? Results.NoContent() : Results.NotFound());

// ----------------------------------------------------------------- scripts

api.MapGet("/examples/script/{family}", (string family) =>
    Enum.TryParse<InstrumentFamily>(family, ignoreCase: true, out var f)
        ? ScriptExamples.ForFamily(f).Select(e => new ExampleDto(e.Name, e.Script)).ToList()
        : []);

api.MapGet("/examples/sequence", () =>
    SequenceExamples.All.Select(e => new ExampleDto(e.Name, e.Script)).ToList());

// What the Script Editor opens on, whatever the instrument: the desktop's worked example.
api.MapGet("/examples/script-start", () => new ExampleDto("Starting script", ScriptExamples.Starting));

api.MapPost("/runs/script", (ScriptRunRequest req, RunService runs) => runs.StartScript(req));

api.MapPost("/runs/sequence", (SequenceRunRequest req, RunService runs) => runs.StartSequence(req));

api.MapPost("/runs/{runId}/stop", (string runId, RunService runs)
    => runs.Stop(runId) ? Results.NoContent() : Results.NotFound());

// A run after the fact: how it ended, and everything it said and recorded, kept for an hour
// after the end. The hub tells the story as it happens to whoever is listening; this is for
// whoever was not — a host that missed one message, or anything that drives the API without a
// hub connection — because a message missed must not be a measurement lost.
api.MapGet("/runs", (RunService runs) => runs.Records());

api.MapGet("/runs/{runId}", (string runId, RunService runs)
    => runs.Record(runId) is { } record ? Results.Ok(record) : Results.NotFound());

api.MapPost("/sequence/requirements", (SequenceRunRequest req, BenchService bench)
    => bench.BindSequence(req.Script, req.Bindings));

// What a script takes from outside, read without running it. The page parses the same lines in
// the browser to draw its boxes; this is for a host that keeps the script as a released
// procedure and has to store what it takes along with it.
api.MapPost("/sequence/inputs", (SequenceRunRequest req) => SequenceRunner.Inputs(req.Script));

// The script language explained. Two languages, so a flag: the multi-instrument one by
// default, which is what the desktop app's Help ▸ Script Language… opens.
api.MapGet("/script-guide", (bool? sequence) =>
{
    bool seq = sequence ?? true;
    return new ScriptGuideDto(
        ScriptGuide.Title(seq),
        ScriptGuide.Lead(seq),
        ScriptGuide.Sections(seq).Select(s => new ScriptSectionDto(s.Heading, s.Prose, s.Example)).ToList());
});

// The Snippets menu: every word the language has, and one press to put it in the editor.
// The language is local to this app — nobody arrives knowing that a sweep is spelled
// FOR f = 100 TO 100k POINTS 40 LOG — and a reference in a separate window is a reference
// nobody opens. A menu that writes the thing for you is read every time.
api.MapGet("/snippets", (bool? sequence) =>
    ScriptLanguage.For(sequence ?? true).Snippets
        .Select(s => new SnippetDto(s.Trigger, s.Title, s.Summary, s.Body))
        .ToList());

// What could come next, for the editor's suggestion list.
//
// Answered by Core rather than by a word list in the browser, and for the reason every other
// shared thing is: two lists of what the language contains would be one list and one guess.
// ScriptLanguage.Complete already knows the keywords, the snippets, the aliases a sequence has
// declared and the names a capture has bound — the editor only has to ask.
//
// The catalog is threaded through where there is one. A console's editor runs against a known
// instrument, so its commands are worth offering; the multi-instrument editor is not tied to one
// and offers the language alone.
api.MapPost("/script/complete", (CompletionRequest req, BenchService bench) =>
{
    var session = req.SessionId is { Length: > 0 } id ? bench.Raw(id) : null;
    var commands = session is null
        ? null
        : CommandReference.ForFamily(session.Family)?.Commands.Select(c => c.Syntax);

    return ScriptLanguage.For(req.Sequence)
        .Complete(req.Script, req.Prefix, commands)
        .Select(c => new CompletionDto(c.Text, c.Detail, c.Kind.ToString(), c.Snippet?.Body))
        .ToList();
});

// ---------------------------------------------------------------------- AI

api.MapGet("/ai", (AiService ai) => ai.Status());

// The settings box writing back. Anyone who can open the page can call this, which is the
// same statement as "anyone who can open the page can spend the key" — there are no accounts
// here, and adding one for this alone would be a lock on one door of an open building. What
// it does police is the endpoint: an absolute http or https address, as the desktop's own
// Apply button insists, because everything downstream hands it to an HttpClient.
api.MapPut("/ai", (AiSettingsUpdate update, AiService ai) => ai.Apply(update));

// Several connections, because the model is a choice: the cheap fast one that suits reading
// commands out of a guide is not the one you want writing a sequence. Adding selects, because
// adding one is how you say you want to use it.
api.MapPost("/ai/connections", (AiService ai, string? provider) => ai.AddConnection(provider));

api.MapDelete("/ai/connections/{id}", (string id, AiService ai) => ai.RemoveConnection(id));

// One selection for the whole server, not one per window — see AiService.SelectConnection.
api.MapPost("/ai/connections/{id}/select", (string id, AiService ai) => ai.SelectConnection(id));

api.MapPost("/ai/script", async (AiScriptRequest req, AiService ai, CancellationToken ct)
    => await ai.WriteScriptAsync(req, ct));

api.MapPost("/ai/extract", async (AiExtractRequest req, AiService ai, CancellationToken ct)
    => await ai.ExtractAsync(req, ct));

// Save Ticked, which is a step of its own because what came out of the datasheet has to be
// read before any of it is written.
api.MapPost("/ai/extract/save", (AiSaveExtractRequest req, AiService ai) => ai.SaveExtracted(req));

app.MapHub<BenchHub>("/hub/bench");

// Instruments are physical things; leaving one in remote mode with its front panel dead is
// the rudest way to shut down. Hand them all back on the way out.
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<BenchService>().DisposeAsync().AsTask().GetAwaiter().GetResult());

app.Run();

/// <summary>
/// The channels named in a waveform request: "1,2" and so on.
///
/// Anything missing, empty or unreadable means channel 1 — what a scope shows first, and what
/// this app captured before it could capture more than one. Bounded to 1..4 because that is what
/// the picker offers and an unbounded list is a way to ask an instrument fifty questions.
/// Ordered and de-duplicated, so "2,1,2" is two channels and draws lowest first.
/// </summary>
static IReadOnlyList<int> ChannelList(string? asked)
{
    if (asked is not { Length: > 0 }) return [1];

    var picked = asked
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(part => int.TryParse(part, out int n) ? n : 0)
        .Where(n => n is >= 1 and <= 4)
        .Distinct()
        .Order()
        .ToList();

    return picked.Count > 0 ? picked : [1];
}

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
