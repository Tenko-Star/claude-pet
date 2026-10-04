using StatusHub.Service.Status;

namespace StatusHub.Service.Tests;

public sealed class HookEventTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryParse_ValidPayload_ReadsSessionId()
    {
        var hookEvent = HookEvent.TryParse("PreToolUse", """{"session_id":"s1","tool_name":"Bash"}""", T0);

        Assert.Equal(new HookEvent("PreToolUse", "s1", T0), hookEvent);
    }

    [Theory]
    [InlineData("""{"session_id":"s1","agent_id":"a1"}""", "a1")]
    [InlineData("""{"session_id":"s1"}""", null)]
    [InlineData("""{"session_id":"s1","agent_id":""}""", null)]
    [InlineData("""{"session_id":"s1","agent_id":42}""", null)]
    public void TryParse_ReadsNonEmptyAgentId(string body, string? expected)
    {
        Assert.Equal(expected, HookEvent.TryParse("PreToolUse", body, T0)?.AgentId);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"tool_name":"Bash"}""")]
    [InlineData("""{"session_id":""}""")]
    [InlineData("""{"session_id":42}""")]
    [InlineData("[]")]
    public void TryParse_UnusableBody_ReturnsNull(string body)
    {
        Assert.Null(HookEvent.TryParse("PreToolUse", body, T0));
    }
}
