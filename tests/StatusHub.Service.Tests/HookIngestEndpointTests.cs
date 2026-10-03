using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using StatusHub.Service.HookIngest;

namespace StatusHub.Service.Tests;

public sealed class HookIngestEndpointTests : IAsyncLifetime
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "statushub-tests", Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private string LogPath => Path.Combine(_dataDirectory, HookEventLog.FileName);

    public async Task InitializeAsync()
    {
        _app = ServiceHost.Build(
        [
            "--HookIngest:Port=0",
            $"--HookIngest:DataDirectory={_dataDirectory}",
        ]);
        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        if (Directory.Exists(_dataDirectory))
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
    }

    [Fact]
    public async Task Post_ValidJson_ReturnsNoContent()
    {
        var response = await PostAsync("SessionStart", """{"session_id":"s1","hook_event_name":"SessionStart"}""");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Post_AppendsOneLinePerRequest()
    {
        var before = DateTimeOffset.UtcNow;
        // Multi-line body: must be compacted into a single JSONL line.
        await PostAsync("PreToolUse", "{\n  \"session_id\": \"s1\",\n  \"tool_name\": \"Bash\"\n}");
        await PostAsync("Stop", """{"session_id":"s1","prompt":"你好"}""");

        var lines = await File.ReadAllLinesAsync(LogPath);
        Assert.Equal(2, lines.Length);

        using var first = JsonDocument.Parse(lines[0]);
        Assert.Equal("PreToolUse", first.RootElement.GetProperty("eventName").GetString());
        Assert.True(first.RootElement.GetProperty("receivedAt").GetDateTimeOffset() >= before);
        var payload = first.RootElement.GetProperty("payload");
        Assert.Equal("s1", payload.GetProperty("session_id").GetString());
        Assert.Equal("Bash", payload.GetProperty("tool_name").GetString());

        using var second = JsonDocument.Parse(lines[1]);
        Assert.Equal("Stop", second.RootElement.GetProperty("eventName").GetString());
        Assert.Equal("你好", second.RootElement.GetProperty("payload").GetProperty("prompt").GetString());
    }

    [Fact]
    public async Task Post_MalformedBody_ReturnsNoContentAndRecordsRawText()
    {
        const string body = "{not json\n";

        var response = await PostAsync("Notification", body);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var line = Assert.Single(await File.ReadAllLinesAsync(LogPath));
        using var record = JsonDocument.Parse(line);
        Assert.Equal("Notification", record.RootElement.GetProperty("eventName").GetString());
        Assert.Equal(body, record.RootElement.GetProperty("rawBody").GetString());
        Assert.False(record.RootElement.TryGetProperty("payload", out _));
    }

    private Task<HttpResponseMessage> PostAsync(string eventName, string body) =>
        _client.PostAsync($"/hooks/{eventName}", new StringContent(body, Encoding.UTF8, "application/json"));
}
