using Microsoft.AspNetCore.SignalR;
using StatusHub.Contracts;

namespace StatusHub.Service.Status;

/// <summary>Server-to-client only. New connections immediately receive the current snapshot.</summary>
public sealed class StatusStreamHub(StatusStore store) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync(
            StatusHubProtocol.SnapshotMethod, store.Current, Context.ConnectionAborted);
        await base.OnConnectedAsync();
    }
}
