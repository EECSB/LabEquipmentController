using LabEquipmentController.Web.Client;
using LabEquipmentController.Web.Client.Services;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

// Everything is served from the host that served this page, so the base address is it.
//
// The client is built rather than newed so that every call goes through ServerWatchHandler,
// which is the one place that notices the server has stopped answering. Registered by hand
// rather than with AddHttpClient: the watch needs the same client it is watching, to ask again
// with, and a factory would hand it a different one.
builder.Services.AddScoped(services =>
{
    var http = new HttpClient(new ServerWatchHandler(services.GetRequiredService<ServerWatch>())
    {
        InnerHandler = new HttpClientHandler()
    })
    {
        BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
    };
    return http;
});

// The two need each other, so the watch is given a way to ask for the client rather than the
// client itself — resolved when something retries, by which time both are built.
builder.Services.AddScoped(services => new ServerWatch(services.GetRequiredService<HttpClient>));

// The AI calls go through the same watch on a client of their own, which has no 100-second
// limit for a model to run into — see AiHttp.
builder.Services.AddScoped(services => new AiHttp(
    new ServerWatchHandler(services.GetRequiredService<ServerWatch>()) { InnerHandler = new HttpClientHandler() },
    new Uri(builder.HostEnvironment.BaseAddress)));

builder.Services.AddScoped<BenchClient>();

// The AI writer's conversations, which outlive the window that shows them — see ScriptChats.
builder.Services.AddScoped<ScriptChats>();

// The AI assistant: what its window stands on (the connections, the relay, the server's kept
// chats), its conversation, which outlives the window as the desktop's hidden window keeps it, and
// the way a script it puts forward reaches the bench page's editors.
builder.Services.AddScoped(services => new WebChatStore(services.GetRequiredService<HttpClient>()));
builder.Services.AddScoped<WebAiService>();
builder.Services.AddScoped<AiPreferences>();
builder.Services.AddScoped<AssistantState>();
builder.Services.AddScoped<ScriptHandoff>();

await builder.Build().RunAsync();
