using StatusHub.Contracts;

namespace StatusHub.Service.Status;

/// <summary>
/// Per-session state machine. Not thread-safe: owned by <see cref="StatusReducerService"/>, which is the only caller.
/// </summary>
/// <remarks>
/// Thinking only covers "prompt received, no tool called yet". Once a tool runs, the turn stays Working until
/// Stop, StopFailure or Notification: Claude Code fires no hook while the model writes the next tool call, so the
/// gap between tools is shown as Working with the <see cref="WorkPace.Composing"/> pace.
/// </remarks>
public sealed class StatusReducer
{
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

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
            case "PreToolUse":
                break;
            case "PostToolUse":
            case "SubagentStop":
                pace = WorkPace.Composing;
                break;
            case "PostToolUseFailure":
                // The tool has finished, as with PostToolUse; the failure itself is a one-shot event.
                pace = WorkPace.Composing;
                oneShot = new StatusEvent(ClaudeStatus.ToolFailure, e.SessionId, e.ReceivedAt);
                break;
            case "Notification":
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
                _sessions.Remove(e.SessionId);
                return null;
            default:
                return null;
        }

        _sessions[e.SessionId] = new SessionState(status, pace, e.ReceivedAt);
        return oneShot;
    }

    /// <summary>
    /// Moves sessions that have been composing for longer than <paramref name="after"/> since their last tool
    /// ended back to Thinking. Returns whether any changed.
    /// </summary>
    public bool FallBackToThinking(DateTimeOffset now, TimeSpan after)
    {
        var changed = false;
        foreach (var (id, session) in _sessions)
        {
            if (session is { Status: ClaudeStatus.Working, Pace: WorkPace.Composing } && now - session.LastSeen > after)
            {
                // Overwriting the current entry during enumeration is allowed for Dictionary.
                _sessions[id] = session with { Status = ClaudeStatus.Thinking, Pace = WorkPace.Active };
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Removes sessions not seen for longer than <paramref name="timeout"/>. Returns whether any were removed.</summary>
    public bool Expire(DateTimeOffset now, TimeSpan timeout)
    {
        var removed = false;
        foreach (var (id, session) in _sessions)
        {
            if (now - session.LastSeen > timeout)
            {
                // Removing the current entry during enumeration is allowed for Dictionary.
                _sessions.Remove(id);
                removed = true;
            }
        }

        return removed;
    }

    /// <summary>Highest-priority status across sessions: Waiting > Working > Thinking > Idle.</summary>
    public ClaudeStatus Aggregate()
    {
        var best = ClaudeStatus.Idle;
        foreach (var session in _sessions.Values)
        {
            if (Priority(session.Status) > Priority(best))
            {
                best = session.Status;
            }
        }

        return best;
    }

    /// <summary>
    /// Pace across Working sessions: Active if any of them runs a tool, Composing if all of them are between tools,
    /// Active when none is Working.
    /// </summary>
    public WorkPace AggregatePace()
    {
        var working = _sessions.Values.Where(s => s.Status == ClaudeStatus.Working).ToList();
        return working.Count > 0 && working.TrueForAll(s => s.Pace == WorkPace.Composing)
            ? WorkPace.Composing
            : WorkPace.Active;
    }

    private static int Priority(ClaudeStatus status) => status switch
    {
        ClaudeStatus.Waiting => 3,
        ClaudeStatus.Working => 2,
        ClaudeStatus.Thinking => 1,
        _ => 0,
    };

    /// <param name="LastSeen">Time of the session's last hook event; for a Composing session, when its last tool ended.</param>
    private readonly record struct SessionState(ClaudeStatus Status, WorkPace Pace, DateTimeOffset LastSeen);
}
