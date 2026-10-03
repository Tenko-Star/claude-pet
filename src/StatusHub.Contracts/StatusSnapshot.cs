namespace StatusHub.Contracts;

/// <summary>Aggregated status across all active sessions. Latest value wins.</summary>
public sealed record StatusSnapshot(ClaudeStatus Status, int ActiveSessions, DateTimeOffset UpdatedAt)
{
    public static StatusSnapshot Initial(DateTimeOffset now) => new(ClaudeStatus.Idle, 0, now);
}
