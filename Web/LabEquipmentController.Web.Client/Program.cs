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
builder.Services.AddScoped<BenchClient>();

// The AI writer's conversations, which outlive the window that shows them — see ScriptChats.
builder.Services.AddScoped<ScriptChats>();

await builder.Build().RunAsync();
