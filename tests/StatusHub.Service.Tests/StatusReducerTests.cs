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
    [InlineData("PostToolUse", ClaudeStatus.Working)]
    [InlineData("PostToolUseFailure", ClaudeStatus.Working)]
    [InlineData("SubagentStop", ClaudeStatus.Working)]
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
    [InlineData("SubagentStop")]
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

        Assert.Null(reducer.Apply(Hook("PreCompact")));

        Assert.Equal(0, reducer.SessionCount);
    }

    [Theory]
    [InlineData("PreToolUse", WorkPace.Active)]
    [InlineData("PostToolUse", WorkPace.Composing)]
    [InlineData("PostToolUseFailure", WorkPace.Composing)]
    [InlineData("SubagentStop", WorkPace.Composing)]
    public void Apply_ToolEvents_SetPace(string eventName, WorkPace expected)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("UserPromptSubmit"));

        reducer.Apply(Hook(eventName, seconds: 1));

        Assert.Equal(expected, reducer.AggregatePace());
    }

    [Theory]
    [InlineData("Stop", ClaudeStatus.Idle)]
    [InlineData("StopFailure", ClaudeStatus.Idle)]
    [InlineData("Notification", ClaudeStatus.Waiting)]
    public void Apply_SubagentStopOutsideATurn_ChangesNothing(string last, ClaudeStatus expected)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PreToolUse"));
        reducer.Apply(Hook(last, seconds: 1));

        Assert.Null(reducer.Apply(Hook("SubagentStop", seconds: 2)));
        Assert.Null(reducer.Apply(Hook("SubagentStop", "unknown", seconds: 2)));

        Assert.Equal(expected, reducer.Aggregate());
        Assert.Equal(1, reducer.SessionCount);
    }

    [Theory]
    [InlineData("idle_prompt")]
    [InlineData("auth_success")]
    public void Apply_NotificationNeedingNoAction_ChangesNothing(string notificationType)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("Stop"));

        reducer.Apply(new HookEvent("Notification", "s1", T0.AddSeconds(60), notificationType));

        Assert.Equal(ClaudeStatus.Idle, reducer.Aggregate());
    }

    [Fact]
    public void Turn_StaysWorkingBetweenToolsUntilStop()
    {
        var reducer = new StatusReducer();

        reducer.Apply(Hook("UserPromptSubmit"));
        Assert.Equal(ClaudeStatus.Thinking, reducer.Aggregate());

        reducer.Apply(Hook("PreToolUse", seconds: 1));
        reducer.Apply(Hook("PostToolUse", seconds: 2));
        Assert.Equal(ClaudeStatus.Working, reducer.Aggregate());

        reducer.Apply(Hook("PreToolUse", seconds: 20));
        Assert.Equal((ClaudeStatus.Working, WorkPace.Active), (reducer.Aggregate(), reducer.AggregatePace()));

        reducer.Apply(Hook("Stop", seconds: 21));
        Assert.Equal(ClaudeStatus.Idle, reducer.Aggregate());
    }

    [Fact]
    public void FallBackToThinking_OnlyAffectsComposingSessionsPastTheThreshold()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PostToolUse", "composing", seconds: 0));
        reducer.Apply(Hook("PreToolUse", "active", seconds: 0));
        reducer.Apply(Hook("PostToolUse", "recent", seconds: 30));
        var threshold = TimeSpan.FromSeconds(45);

        Assert.False(reducer.FallBackToThinking(T0.AddSeconds(45), threshold));
        Assert.True(reducer.FallBackToThinking(T0.AddSeconds(46), threshold));
        Assert.False(reducer.FallBackToThinking(T0.AddSeconds(46), threshold));

        // "active" still runs a tool and "recent" is within the threshold; both stay Working.
        Assert.Equal(ClaudeStatus.Working, reducer.Aggregate());
        reducer.Apply(Hook("SessionEnd", "active", seconds: 47));
        reducer.Apply(Hook("SessionEnd", "recent", seconds: 47));
        Assert.Equal(ClaudeStatus.Thinking, reducer.Aggregate());
    }

    [Fact]
    public void FallBackToThinking_IsUndoneByTheNextToolCall()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PostToolUse"));
        reducer.FallBackToThinking(T0.AddSeconds(60), TimeSpan.FromSeconds(45));

        reducer.Apply(Hook("PreToolUse", seconds: 61));

        Assert.Equal((ClaudeStatus.Working, WorkPace.Active), (reducer.Aggregate(), reducer.AggregatePace()));
    }

    [Theory]
    [InlineData("PreToolUse", "PostToolUse", WorkPace.Active)]
    [InlineData("PostToolUse", "SubagentStop", WorkPace.Composing)]
    [InlineData("PostToolUse", "UserPromptSubmit", WorkPace.Composing)]
    [InlineData("UserPromptSubmit", "Notification", WorkPace.Active)]
    public void AggregatePace_IsActiveIfAnyWorkingSessionIsActive(string first, string second, WorkPace expected)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook(first, "a"));
        reducer.Apply(Hook(second, "b"));

        Assert.Equal(expected, reducer.AggregatePace());
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
