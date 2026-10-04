using StatusHub.Contracts;

namespace StatusHub.Service.Status;

/// <summary>
/// Per-session state machine. Not thread-safe: owned by <see cref="StatusReducerService"/>, which is the only caller.
/// </summary>
/// <remarks>
/// Each session tracks its main thread and, by <c>agent_id</c>, each of its subagents. SubagentStart, SubagentStop
/// and tool events carrying an agent id belong to that subagent; every other event belongs to the main thread.
/// A session shows the highest-priority status among its threads (Waiting > Working > Thinking > Idle), so subagents
/// still running after the main thread's Stop keep the session Working.
/// Thinking only covers "prompt received, no tool called yet". Once a tool runs, a thread stays Working until
/// Stop, StopFailure or Notification (main thread) or SubagentStop (subagent): Claude Code fires no hook while the
/// model writes the next tool call, so the gap between tools is shown as Working with the <see cref="WorkPace.Composing"/> pace.
/// Notifications of type <c>idle_prompt</c> and <c>auth_success</c> need no action and leave the status unchanged.
/// Only the primary session is displayed and produces one-shot events. The first session to send an event becomes
/// primary; when it ends or expires, the most recently active remaining session takes over.
/// </remarks>
public sealed class StatusReducer
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);

    // Null exactly when there are no sessions.
    private string? _primaryId;

    public int SessionCount => _sessions.Count;

    /// <summary>Applies one hook event. Returns the one-shot event it produces, if any.</summary>
    public StatusEvent? Apply(HookEvent e)
    {
        var status = ClaudeStatus.Working;
        var pace = WorkPace.Active;
        StatusEvent? oneShot = null;

        switch (e.EventName)
        {
            case "SessionStart":
                status = ClaudeStatus.Idle;
                break;
            case "UserPromptSubmit":
                status = ClaudeStatus.Thinking;
                break;
            case "SubagentStart":
                if (e.AgentId is null)
                {
                    return null;
                }

                status = ClaudeStatus.Thinking;
                break;
            case "PreToolUse":
                break;
            case "PostToolUse":
                pace = WorkPace.Composing;
                break;
            case "PostToolUseFailure":
                // The tool has finished, as with PostToolUse; the failure itself is a one-shot event.
                pace = WorkPace.Composing;
                oneShot = new StatusEvent(ClaudeStatus.ToolFailure, e.SessionId, e.ReceivedAt);
                break;
            case "SubagentStop":
                if (e.AgentId is not null
                    && _sessions.TryGetValue(e.SessionId, out var session)
                    && session.Agents.Remove(e.AgentId))
                {
                    session.LastActivity = e.ReceivedAt;
                }

                return null;
            case "Notification":
                if (e.NotificationType is "idle_prompt" or "auth_success")
                {
                    return null;
                }

                status = ClaudeStatus.Waiting;
                break;
            case "Stop":
                status = ClaudeStatus.Idle;
                oneShot = new StatusEvent(ClaudeStatus.Done, e.SessionId, e.ReceivedAt);
                break;
            case "StopFailure":
                status = ClaudeStatus.Idle;
                oneShot = new StatusEvent(ClaudeStatus.Error, e.SessionId, e.ReceivedAt);
                break;
            case "SessionEnd":
                Remove(e.SessionId);
                return null;
            default:
                return null;
        }

        var target = GetOrCreate(e.SessionId, e.ReceivedAt);
        target.LastActivity = e.ReceivedAt;

        var worker = new WorkerState(status, pace, e.ReceivedAt);
        if (e.AgentId is not null
            && e.EventName is ("SubagentStart" or "PreToolUse" or "PostToolUse" or "PostToolUseFailure"))
        {
            target.Agents[e.AgentId] = worker;
        }
        else
        {
            target.Main = worker;
        }

        return e.SessionId == _primaryId ? oneShot : null;
    }

    /// <summary>
    /// Moves threads (main threads and subagents) that have been composing for longer than <paramref name="after"/>
    /// since their last tool ended back to Thinking. Returns whether any changed.
    /// </summary>
    public bool FallBackToThinking(DateTimeOffset now, TimeSpan after)
    {
        var changed = false;
        foreach (var session in _sessions.Values)
        {
            if (ShouldFallBack(session.Main, now, after))
            {
                session.Main = FallBack(session.Main);
                changed = true;
            }

            foreach (var (id, agent) in session.Agents)
            {
                if (ShouldFallBack(agent, now, after))
                {
                    // Overwriting the current entry during enumeration is allowed for Dictionary.
                    session.Agents[id] = FallBack(agent);
                    changed = true;
                }
            }
        }

        return changed;
    }

    /// <summary>
    /// Removes sessions with no event for longer than <paramref name="sessionTimeout"/> and subagents with no event
    /// for longer than <paramref name="subagentTimeout"/>. Returns whether anything was removed.
    /// </summary>
    public bool Expire(DateTimeOffset now, TimeSpan sessionTimeout, TimeSpan subagentTimeout)
    {
        var removed = false;
        foreach (var (id, session) in _sessions)
        {
            if (now - session.LastActivity > sessionTimeout)
            {
                // Removing the current entry during enumeration is allowed for Dictionary.
                _sessions.Remove(id);
                removed = true;
                continue;
            }

            foreach (var (agentId, agent) in session.Agents)
            {
                if (now - agent.LastSeen > subagentTimeout)
                {
                    session.Agents.Remove(agentId);
                    removed = true;
                }
            }
        }

        if (_primaryId is not null && !_sessions.ContainsKey(_primaryId))
        {
            Rebind();
        }

        return removed;
    }

    /// <summary>Status of the primary session; Idle when there is none.</summary>
    public ClaudeStatus Aggregate() => Primary() is { } session ? Effective(session).Status : ClaudeStatus.Idle;

    /// <summary>
    /// Pace of the primary session: Active if any of its Working threads runs a tool, Composing if all of them are
    /// between tools, Active when none is Working or there is no primary session.
    /// </summary>
    public WorkPace AggregatePace() => Primary() is { } session ? Effective(session).Pace : WorkPace.Active;

    private Session GetOrCreate(string id, DateTimeOffset now)
    {
        if (!_sessions.TryGetValue(id, out var session))
        {
            session = new Session(new WorkerState(ClaudeStatus.Idle, WorkPace.Active, now), now);
            _sessions[id] = session;
            _primaryId ??= id;
        }

        return session;
    }

    private void Remove(string id)
    {
        if (_sessions.Remove(id) && id == _primaryId)
        {
            Rebind();
        }
    }

    /// <summary>Makes the most recently active session primary, or none when no session is left.</summary>
    private void Rebind() =>
        _primaryId = _sessions.Count == 0 ? null : _sessions.MaxBy(pair => pair.Value.LastActivity).Key;

    private Session? Primary() => _primaryId is not null ? _sessions.GetValueOrDefault(_primaryId) : null;

    private static (ClaudeStatus Status, WorkPace Pace) Effective(Session session)
    {
        var best = session.Main.Status;
        var anyActive = session.Main is { Status: ClaudeStatus.Working, Pace: WorkPace.Active };
        foreach (var agent in session.Agents.Values)
        {
            if (Priority(agent.Status) > Priority(best))
            {
                best = agent.Status;
            }

            anyActive |= agent is { Status: ClaudeStatus.Working, Pace: WorkPace.Active };
        }

        return (best, best == ClaudeStatus.Working && !anyActive ? WorkPace.Composing : WorkPace.Active);
    }

    private static bool ShouldFallBack(WorkerState worker, DateTimeOffset now, TimeSpan after) =>
        worker is { Status: ClaudeStatus.Working, Pace: WorkPace.Composing } && now - worker.LastSeen > after;

    private static WorkerState FallBack(WorkerState worker) =>
        worker with { Status = ClaudeStatus.Thinking, Pace = WorkPace.Active };

    private static int Priority(ClaudeStatus status) => status switch
    {
        ClaudeStatus.Waiting => 3,
        ClaudeStatus.Working => 2,
        ClaudeStatus.Thinking => 1,
        _ => 0,
    };

    /// <summary>State of one thread: the main thread or a subagent.</summary>
    /// <param name="LastSeen">Time of the thread's last hook event; for a Composing thread, when its last tool ended.</param>
    private readonly record struct WorkerState(ClaudeStatus Status, WorkPace Pace, DateTimeOffset LastSeen);

    /// <summary>
    /// One session: its main thread, its subagents by agent id, and the time of its last hook event from any thread.
    /// </summary>
    private sealed class Session(WorkerState main, DateTimeOffset lastActivity)
    {
        public WorkerState Main { get; set; } = main;

        public Dictionary<string, WorkerState> Agents { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset LastActivity { get; set; } = lastActivity;
    }
}
