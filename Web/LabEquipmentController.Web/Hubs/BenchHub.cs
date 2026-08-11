using Microsoft.AspNetCore.SignalR;

namespace LabEquipmentController.Web.Hubs;

/// <summary>
/// The push channel: scan hits as they are found, and script output as it happens.
/// </summary>
/// <remarks>
/// Clients join a group per run rather than receiving everything, so two people watching
/// two different sweeps do not see each other's lines. Joining is by run id, which is
/// handed back by the call that started the run — there is nothing to guess at.
/// </remarks>
public sealed class BenchHub : Hub
{
    public Task Watch(string runId) => Groups.AddToGroupAsync(Context.ConnectionId, runId);

    public Task Unwatch(string runId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, runId);
}
