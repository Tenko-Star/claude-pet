using StatusHub.Contracts;

namespace StatusHub.Service.Status;

/// <summary>
/// Per-session state machine. Not thread-safe: owned by <see cref="StatusReducerService"/>, which is the only caller.
/// </summary>
public sealed class StatusReducer
{
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);

    public int SessionCount => _sessions.Count;

    /// <summary>Applies one hook event. Returns the one-shot event it produces, if any.</summary>
    public StatusEvent? Apply(HookEvent e)
    {
        ClaudeStatus status;
        StatusEvent? oneShot = null;

        switch (e.EventName)
        {
            case "SessionStart":
                status = ClaudeStatus.Idle;
                break;
            case "UserPromptSubmit":
            case "PostToolUse":
                status = ClaudeStatus.Thinking;
                break;
            case "PreToolUse":
                status = ClaudeStatus.Working;
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

        _sessions[e.SessionId] = new SessionState(status, e.ReceivedAt);
        return oneShot;
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

    private static int Priority(ClaudeStatus status) => status switch
    {
        ClaudeStatus.Waiting => 3,
        ClaudeStatus.Working => 2,
        ClaudeStatus.Thinking => 1,
        _ => 0,
    };

    private readonly record struct SessionState(ClaudeStatus Status, DateTimeOffset LastSeen);
}
