using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StatusHub.Contracts;

namespace DeskPet.App.Status;

/// <summary>
/// Subscribes to the StatusHub stream and forwards it to <see cref="MainWindow"/> on the UI thread.
/// Works whether the service starts before or after the app: the first connect is retried until it
/// succeeds, and a lost connection is retried forever.
/// </summary>
public sealed class StatusClientService(
    MainWindow window,
    IOptions<DeskPetOptions> options,
    ILogger<StatusClientService> logger) : BackgroundService
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hubUrl = options.Value.HubUrl;
        await using var connection = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect(new ForeverRetryPolicy())
            .Build();

        connection.On<StatusSnapshot>(StatusHubProtocol.SnapshotMethod, snapshot => OnUiThread(() => window.ApplySnapshot(snapshot)));
        connection.On<StatusEvent>(StatusHubProtocol.EventMethod, statusEvent => OnUiThread(() => window.ApplyEvent(statusEvent)));
        connection.Reconnecting += error =>
        {
            logger.LogWarning(error, "Lost the status hub connection; reconnecting.");
            OnUiThread(window.ApplyDisconnected);
            return Task.CompletedTask;
        };
        connection.Reconnected += _ =>
        {
            logger.LogInformation("Reconnected to the status hub.");
            return Task.CompletedTask;
        };

        // Automatic reconnect never gives up with ForeverRetryPolicy, so Closed means the connection
        // was stopped or the initial handshake failed; this loop then connects again from scratch.
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ =>
        {
            OnUiThread(window.ApplyDisconnected);
            closed.TrySetResult();
            return Task.CompletedTask;
        };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.StartAsync(stoppingToken);
                logger.LogInformation("Connected to the status hub at {HubUrl}.", hubUrl);
                await closed.Task.WaitAsync(stoppingToken);
                closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not connect to the status hub at {HubUrl}.", hubUrl);
            }

            try
            {
                await Task.Delay(ConnectRetryDelay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void OnUiThread(Action action) => _ = window.Dispatcher.InvokeAsync(action);

    /// <summary>Reconnects immediately, then after 2 s, 5 s and every 10 s after that.</summary>
    private sealed class ForeverRetryPolicy : IRetryPolicy
    {
        private static readonly TimeSpan[] Delays = [TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) =>
            Delays[(int)Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1)];
    }
}
