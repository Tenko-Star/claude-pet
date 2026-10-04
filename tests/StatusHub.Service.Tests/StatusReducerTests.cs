using StatusHub.Contracts;
using StatusHub.Service.Status;

namespace StatusHub.Service.Tests;

public sealed class StatusReducerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan LongTimeout = TimeSpan.FromHours(1);

    private static HookEvent Hook(string eventName, string sessionId = "s1", int seconds = 0) =>
        new(eventName, sessionId, T0.AddSeconds(seconds));

    private static HookEvent Agent(string eventName, string agentId, string sessionId = "s1", int seconds = 0) =>
        new(eventName, sessionId, T0.AddSeconds(seconds), AgentId: agentId);

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
    [InlineData("SubagentStart")]
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
    public void Apply_ToolEvents_SetPace(string eventName, WorkPace expected)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("UserPromptSubmit"));

        reducer.Apply(Hook(eventName, seconds: 1));

        Assert.Equal(expected, reducer.AggregatePace());
    }

    [Fact]
    public void Apply_SubagentEventsWithoutAKnownAgent_ChangeNothing()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PreToolUse"));

        Assert.Null(reducer.Apply(Hook("SubagentStart", seconds: 1)));
        Assert.Null(reducer.Apply(Hook("SubagentStop", seconds: 1)));
        Assert.Null(reducer.Apply(Agent("SubagentStop", "unknown", seconds: 1)));
        Assert.Null(reducer.Apply(Agent("SubagentStop", "a1", "unknown", seconds: 1)));

        Assert.Equal((ClaudeStatus.Working, WorkPace.Active), (reducer.Aggregate(), reducer.AggregatePace()));
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
    public void Subagents_KeepSessionWorkingAfterMainStop()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("UserPromptSubmit"));
        reducer.Apply(Agent("PreToolUse", "a1", seconds: 1));

        Assert.Equal(new StatusEvent(ClaudeStatus.Done, "s1", T0.AddSeconds(2)), reducer.Apply(Hook("Stop", seconds: 2)));
        Assert.Equal((ClaudeStatus.Working, WorkPace.Active), (reducer.Aggregate(), reducer.AggregatePace()));

        reducer.Apply(Agent("PostToolUse", "a1", seconds: 3));
        Assert.Equal((ClaudeStatus.Working, WorkPace.Composing), (reducer.Aggregate(), reducer.AggregatePace()));

        reducer.Apply(Agent("SubagentStop", "a1", seconds: 4));
        Assert.Equal(ClaudeStatus.Idle, reducer.Aggregate());
    }

    [Fact]
    public void Subagent_ToolFailure_ReturnsOneShotEvent()
    {
        var oneShot = new StatusReducer().Apply(Agent("PostToolUseFailure", "a1", seconds: 5));

        Assert.Equal(new StatusEvent(ClaudeStatus.ToolFailure, "s1", T0.AddSeconds(5)), oneShot);
    }

    [Fact]
    public void SessionStart_KeepsRunningSubagents()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Agent("PreToolUse", "a1"));

        reducer.Apply(Hook("SessionStart", seconds: 1));

        Assert.Equal(ClaudeStatus.Working, reducer.Aggregate());
    }

    [Fact]
    public void FallBackToThinking_OnlyAffectsComposingThreadsPastTheThreshold()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PostToolUse", seconds: 0));
        reducer.Apply(Agent("PreToolUse", "active", seconds: 0));
        reducer.Apply(Agent("PostToolUse", "recent", seconds: 30));
        var threshold = TimeSpan.FromSeconds(45);

        Assert.False(reducer.FallBackToThinking(T0.AddSeconds(45), threshold));
        Assert.True(reducer.FallBackToThinking(T0.AddSeconds(46), threshold));
        Assert.False(reducer.FallBackToThinking(T0.AddSeconds(46), threshold));

        // "active" still runs a tool and "recent" is within the threshold; both stay Working.
        Assert.Equal(ClaudeStatus.Working, reducer.Aggregate());
        reducer.Apply(Agent("SubagentStop", "active", seconds: 47));
        Assert.Equal((ClaudeStatus.Working, WorkPace.Composing), (reducer.Aggregate(), reducer.AggregatePace()));
        reducer.Apply(Agent("SubagentStop", "recent", seconds: 47));
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
    [InlineData("PostToolUse", "PreToolUse", WorkPace.Active)]
    [InlineData("PostToolUse", "PostToolUse", WorkPace.Composing)]
    [InlineData("Stop", "PostToolUse", WorkPace.Composing)]
    public void AggregatePace_IsActiveIfAnyWorkingThreadIsActive(string main, string agent, WorkPace expected)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook(main));
        reducer.Apply(Agent(agent, "a1", seconds: 1));

        Assert.Equal(expected, reducer.AggregatePace());
    }

    [Theory]
    [InlineData("Notification", "PreToolUse", ClaudeStatus.Waiting)]
    [InlineData("UserPromptSubmit", "PreToolUse", ClaudeStatus.Working)]
    [InlineData("Stop", "SubagentStart", ClaudeStatus.Thinking)]
    [InlineData("Stop", "PostToolUse", ClaudeStatus.Working)]
    public void Aggregate_PicksHighestPriorityAcrossThreads(string main, string agent, ClaudeStatus expected)
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook(main));
        reducer.Apply(Agent(agent, "a1", seconds: 1));

        Assert.Equal(expected, reducer.Aggregate());
        Assert.Equal(1, reducer.SessionCount);
    }

    [Fact]
    public void Primary_IsTheFirstSession_OtherSessionsAreIgnored()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("UserPromptSubmit", "a"));
        reducer.Apply(Hook("Notification", "b", seconds: 1));
        reducer.Apply(Agent("PreToolUse", "a1", "b", seconds: 1));

        Assert.Equal(ClaudeStatus.Thinking, reducer.Aggregate());
        Assert.Equal(2, reducer.SessionCount);
        Assert.Null(reducer.Apply(Hook("Stop", "b", seconds: 2)));
        Assert.Null(reducer.Apply(Agent("PostToolUseFailure", "a1", "b", seconds: 2)));
        Assert.NotNull(reducer.Apply(Hook("Stop", "a", seconds: 3)));
    }

    [Fact]
    public void Primary_SessionEnd_SwitchesToMostRecentlyActiveSession()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PreToolUse", "a", seconds: 0));
        reducer.Apply(Hook("Notification", "b", seconds: 1));
        reducer.Apply(Hook("UserPromptSubmit", "c", seconds: 2));

        reducer.Apply(Hook("SessionEnd", "a", seconds: 3));

        Assert.Equal(ClaudeStatus.Thinking, reducer.Aggregate());
        Assert.NotNull(reducer.Apply(Hook("Stop", "c", seconds: 4)));
        Assert.Null(reducer.Apply(Hook("Stop", "b", seconds: 5)));
    }

    [Fact]
    public void Primary_WithNoSessionLeft_IsIdleUntilTheNextEventBindsOne()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PreToolUse", "a"));
        reducer.Apply(Hook("SessionEnd", "a", seconds: 1));

        Assert.Equal(ClaudeStatus.Idle, reducer.Aggregate());

        reducer.Apply(Hook("UserPromptSubmit", "b", seconds: 2));
        Assert.Equal(ClaudeStatus.Thinking, reducer.Aggregate());
    }

    [Fact]
    public void Expire_RemovesOnlyStaleSessionsAndSwitchesPrimary()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("PreToolUse", "old", seconds: 0));
        reducer.Apply(Hook("UserPromptSubmit", "fresh", seconds: 50));

        var removed = reducer.Expire(T0.AddSeconds(70), TimeSpan.FromSeconds(60), LongTimeout);

        Assert.True(removed);
        Assert.Equal(1, reducer.SessionCount);
        Assert.Equal(ClaudeStatus.Thinking, reducer.Aggregate());
        Assert.False(reducer.Expire(T0.AddSeconds(70), TimeSpan.FromSeconds(60), LongTimeout));
    }

    [Fact]
    public void Expire_RemovesSilentSubagents()
    {
        var reducer = new StatusReducer();
        reducer.Apply(Hook("Stop"));
        reducer.Apply(Agent("SubagentStart", "silent", seconds: 0));
        reducer.Apply(Agent("PreToolUse", "busy", seconds: 50));

        Assert.True(reducer.Expire(T0.AddSeconds(70), LongTimeout, TimeSpan.FromSeconds(60)));
        Assert.Equal(ClaudeStatus.Working, reducer.Aggregate());

        Assert.True(reducer.Expire(T0.AddSeconds(111), LongTimeout, TimeSpan.FromSeconds(60)));
        Assert.Equal(ClaudeStatus.Idle, reducer.Aggregate());
        Assert.Equal(1, reducer.SessionCount);
    }
}
