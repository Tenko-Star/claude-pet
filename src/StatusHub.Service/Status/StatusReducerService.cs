using System.Threading.Channels;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using StatusHub.Contracts;

namespace StatusHub.Service.Status;

/// <summary>
/// Single consumer of the hook event channel. Owns the <see cref="StatusReducer"/>, so its state needs no locks,
/// publishes snapshots to <see cref="StatusStore"/> and broadcasts snapshots and events to hub clients.
/// </summary>
public sealed class StatusReducerService(
    ChannelReader<HookEvent> reader,
    StatusStore store,
    IHubContext<StatusStreamHub> hub,
    IOptions<StatusOptions> options,
    TimeProvider time,
    ILogger<StatusReducerService> logger) : BackgroundService
{
    private readonly StatusReducer _reducer = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.ExpiryScanInterval, time);

        // Keep one pending wait per source and only replace the one that completed.
        var readTask = reader.WaitToReadAsync(stoppingToken).AsTask();
        var tickTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();

        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(readTask, tickTask);

                if (completed == readTask)
                {
                    if (!await readTask)
                    {
                        return; // Channel completed.
                    }

                    while (reader.TryRead(out var hookEvent))
                    {
                        var oneShot = _reducer.Apply(hookEvent);
                        if (logger.IsEnabled(LogLevel.Debug))
                        {
                            logger.LogDebug(
                                "Applied {EventName} for session {SessionId}; aggregate {Status}",
                                hookEvent.EventName, hookEvent.SessionId, _reducer.Aggregate());
                        }

                        if (oneShot is not null)
                        {
                            await BroadcastAsync(StatusHubProtocol.EventMethod, oneShot, stoppingToken);
                        }

                        await PublishIfChangedAsync(stoppingToken);
                    }

                    readTask = reader.WaitToReadAsync(stoppingToken).AsTask();
                }
                else
                {
                    if (!await tickTask)
                    {
                        return; // Timer disposed.
                    }

                    if (_reducer.Expire(time.GetUtcNow(), settings.SessionTimeout))
                    {
                        logger.LogInformation("Expired stale sessions; {Count} remain", _reducer.SessionCount);
                        await PublishIfChangedAsync(stoppingToken);
                    }

                    tickTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PublishIfChangedAsync(CancellationToken cancellationToken)
    {
        var status = _reducer.Aggregate();
        var sessions = _reducer.SessionCount;
        var current = store.Current;
        if (current.Status == status && current.ActiveSessions == sessions)
        {
            return;
        }

        // Store first, so a client connecting now gets the new value even if it misses the broadcast.
        var snapshot = new StatusSnapshot(status, sessions, time.GetUtcNow());
        store.Current = snapshot;
        await BroadcastAsync(StatusHubProtocol.SnapshotMethod, snapshot, cancellationToken);
    }

    private async Task BroadcastAsync(string method, object message, CancellationToken cancellationToken)
    {
        try
        {
            await hub.Clients.All.SendAsync(method, message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Broadcasting {Method} failed", method);
        }
    }
}
