using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR.Client;
using StatusHub.Contracts;

namespace StatusHub.Service.Tests;

public sealed class StatusHubTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "statushub-tests", Guid.NewGuid().ToString("N"));

    private readonly Channel<StatusSnapshot> _snapshots = Channel.CreateUnbounded<StatusSnapshot>();
    private readonly Channel<StatusEvent> _events = Channel.CreateUnbounded<StatusEvent>();

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private HubConnection _connection = null!;

    public async Task InitializeAsync()
    {
        _app = ServiceHost.Build(
        [
            "--HookIngest:Port=0",
            $"--HookIngest:DataDirectory={_dataDirectory}",
        ]);
        await _app.StartAsync();

        var baseAddress = new Uri(_app.Urls.First());
        _client = new HttpClient { BaseAddress = baseAddress };

        _connection = new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, StatusHubProtocol.Path))
            .Build();
        _connection.On<StatusSnapshot>(StatusHubProtocol.SnapshotMethod, s => _snapshots.Writer.TryWrite(s));
        _connection.On<StatusEvent>(StatusHubProtocol.EventMethod, e => _events.Writer.TryWrite(e));
        await _connection.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _connection.DisposeAsync();
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Connect_ReceivesInitialIdleSnapshot()
    {
        var snapshot = await NextAsync(_snapshots);

        Assert.Equal(ClaudeStatus.Idle, snapshot.Status);
        Assert.Equal(0, snapshot.ActiveSessions);
    }

    [Fact]
    public async Task HookEvents_DriveSnapshotsAndEvents()
    {
        await NextAsync(_snapshots); // Initial snapshot.

        await PostAsync("PreToolUse", """{"session_id":"s1","tool_name":"Bash"}""");
        var working = await NextAsync(_snapshots);
        Assert.Equal(ClaudeStatus.Working, working.Status);
        Assert.Equal(WorkPace.Active, working.Pace);
        Assert.Equal(1, working.ActiveSessions);

        await PostAsync("Stop", """{"session_id":"s1"}""");
        var done = await NextAsync(_events);
        Assert.Equal(ClaudeStatus.Done, done.Kind);
        Assert.Equal("s1", done.SessionId);
        var idle = await NextAsync(_snapshots);
        Assert.Equal(ClaudeStatus.Idle, idle.Status);
        Assert.Equal(1, idle.ActiveSessions);
    }

    private static async Task<T> NextAsync<T>(Channel<T> channel)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await channel.Reader.ReadAsync(cts.Token);
    }

    private async Task PostAsync(string eventName, string body)
    {
        using var response = await _client.PostAsync(
            $"/hooks/{eventName}", new StringContent(body, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }
}
