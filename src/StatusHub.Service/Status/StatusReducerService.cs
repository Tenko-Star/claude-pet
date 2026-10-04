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
    /// <summary>
    /// How often Composing sessions are checked against <see cref="StatusOptions.ThinkFallback"/> and an Idle primary
    /// against <see cref="StatusOptions.PrimaryIdleRebind"/>.
    /// </summary>
    public static readonly TimeSpan FallbackCheckInterval = TimeSpan.FromSeconds(1);

    private readonly StatusReducer _reducer = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        using var timer = new PeriodicTimer(settings.ExpiryScanInterval, time);
        using var fallbackTimer = new PeriodicTimer(FallbackCheckInterval, time);

        // Keep one pending wait per source and only replace the one that completed.
        var readTask = reader.WaitToReadAsync(stoppingToken).AsTask();
        var tickTask = timer.WaitForNextTickAsync(stoppingToken).AsTask();
        var fallbackTask = fallbackTimer.WaitForNextTickAsync(stoppingToken).AsTask();

        try
        {
            while (true)
            {
                var completed = await Task.WhenAny(readTask, tickTask, fallbackTask);

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

                        _reducer.RebindIdlePrimary(time.GetUtcNow(), settings.PrimaryIdleRebind);
                        await PublishIfChangedAsync(stoppingToken);
                    }

                    readTask = reader.WaitToReadAsync(stoppingToken).AsTask();
                }
                else if (completed == fallbackTask)
                {
                    if (!await fallbackTask)
                    {
                        return; // Timer disposed.
                    }

                    var now = time.GetUtcNow();
                    var fellBack = _reducer.FallBackToThinking(now, settings.ThinkFallback);
                    if (_reducer.RebindIdlePrimary(now, settings.PrimaryIdleRebind) || fellBack)
                    {
                        await PublishIfChangedAsync(stoppingToken);
                    }

                    fallbackTask = fallbackTimer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                else
                {
                    if (!await tickTask)
                    {
                        return; // Timer disposed.
                    }

                    if (_reducer.Expire(time.GetUtcNow(), settings.SessionTimeout, settings.SubagentTimeout))
                    {
                        logger.LogInformation(
                            "Expired stale sessions or subagents; {Count} sessions remain", _reducer.SessionCount);
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
        var pace = status == ClaudeStatus.Working ? _reducer.AggregatePace() : WorkPace.Active;
        var sessions = _reducer.SessionCount;
        var list = _reducer.Sessions();
        var primary = _reducer.PrimaryId;
        var current = store.Current;
        if (current.Status == status && current.Pace == pace && current.ActiveSessions == sessions
            && current.PrimarySessionId == primary && SameSessions(current.Sessions ?? [], list))
        {
            return;
        }

        // Store first, so a client connecting now gets the new value even if it misses the broadcast.
        var snapshot = new StatusSnapshot(status, sessions, time.GetUtcNow(), pace, list, primary);
        store.Current = snapshot;
        await BroadcastAsync(StatusHubProtocol.SnapshotMethod, snapshot, cancellationToken);
    }

    // Records holding lists compare those lists by reference, so compare element by element.
    private static bool SameSessions(IReadOnlyList<SessionInfo> a, IReadOnlyList<SessionInfo> b) =>
        a.Count == b.Count
        && a.Zip(b).All(pair => pair.First.SessionId == pair.Second.SessionId
            && pair.First.Failed == pair.Second.Failed
            && pair.First.Subagents.SequenceEqual(pair.Second.Subagents));

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
