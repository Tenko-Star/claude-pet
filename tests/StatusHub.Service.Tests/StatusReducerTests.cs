using StatusHub.Contracts;
using StatusHub.Service.Status;

namespace StatusHub.Service.Tests;

public sealed class StatusReducerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static HookEvent Hook(string eventName, string sessionId = "s1", int seconds = 0) =>
        new(eventName, sessionId, T0.AddSeconds(seconds));

    [Fact]
    public void Empty_AggregatesToIdle()
    {
        var reducer = new StatusReducer();

        Assert.Equal(ClaudeStatus.Idle, reducer.Aggregate());
        Assert.Equal(0, reducer.SessionCount);
    }

    [Theory]
    [InlineData("SessionStart", ClaudeStatus.Idle)]
    [InlineData("UserPromptSubmit", ClaudeStatus.Thinking)]
    [InlineData("PreToolUse", ClaudeStatus.Working)]
    [InlineData("PostToolUse", ClaudeStatus.Thinking)]
    [InlineData("PostToolUseFailure", ClaudeStatus.Thinking)]
    [InlineData("Notification", ClaudeStatus.Waiting)]
    [InlineData("Stop", ClaudeStatus.Idle)]
    [InlineData("StopFailure", ClaudeStatus.Idle)]
    public void Apply_MapsEventToSessionStatus(string eventName, ClaudeStatus expected)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PreToolUse"));

        reducer.Apply(Hook(eventName, seconds: 1));

        Assert.Equal(expected, reducer.Aggregate());
        Assert.Equal(1, reducer.SessionCount);
    }

    [Theory]
    [InlineData("Stop", ClaudeStatus.Done)]
    [InlineData("StopFailure", ClaudeStatus.Error)]
    [InlineData("PostToolUseFailure", ClaudeStatus.ToolFailure)]
    public void Apply_StopEvents_ReturnOneShotEvent(string eventName, ClaudeStatus kind)
    {
        var reducer = new StatusReducer();

        var oneShot = reducer.Apply(Hook(eventName, seconds: 5));

        Assert.Equal(new StatusEvent(kind, "s1", T0.AddSeconds(5)), oneShot);
    }

    [Theory]
    [InlineData("SessionStart")]
    [InlineData("UserPromptSubmit")]
    [InlineData("PreToolUse")]
    [InlineData("PostToolUse")]
    [InlineData("Notification")]
    [InlineData("SessionEnd")]
    public void Apply_OtherEvents_ReturnNoOneShotEvent(string eventName)
    {
        Assert.Null(new StatusReducer().Apply(Hook(eventName)));
    }

    [Fact]
    public void Apply_SessionEnd_RemovesSession()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PreToolUse"));

        reducer.Apply(Hook("SessionEnd", seconds: 1));

        Assert.Equal(0, reducer.SessionCount);
        Assert.Equal(ClaudeStatus.Idle, reducer.Aggregate());
    }

    [Fact]
    public void Apply_UnknownEvent_ChangesNothing()
    {
        var reducer = new StatusReducer();

        Assert.Null(reducer.Apply(Hook("SubagentStop")));

        Assert.Equal(0, reducer.SessionCount);
    }

    [Theory]
    [InlineData("Notification", "PreToolUse", ClaudeStatus.Waiting)]
    [InlineData("PreToolUse", "UserPromptSubmit", ClaudeStatus.Working)]
    [InlineData("UserPromptSubmit", "SessionStart", ClaudeStatus.Thinking)]
    public void Aggregate_PicksHighestPriorityAcrossSessions(string first, string second, ClaudeStatus expected)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook(first, "a"));
        reducer.Apply(Hook(second, "b"));

        Assert.Equal(expected, reducer.Aggregate());
        Assert.Equal(2, reducer.SessionCount);
    }

    [Fact]
    public void Expire_RemovesOnlyStaleSessions()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PreToolUse", "old", seconds: 0));
        reducer.Apply(Hook("UserPromptSubmit", "fresh", seconds: 50));

        var removed = reducer.Expire(T0.AddSeconds(70), TimeSpan.FromSeconds(60));

        Assert.True(removed);
        Assert.Equal(1, reducer.SessionCount);
        Assert.Equal(ClaudeStatus.Thinking, reducer.Aggregate());
        Assert.False(reducer.Expire(T0.AddSeconds(70), TimeSpan.FromSeconds(60)));
    }
}
