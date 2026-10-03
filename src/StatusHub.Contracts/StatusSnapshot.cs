namespace StatusHub.Contracts;

/// <summary>Aggregated status across all active sessions. Latest value wins.</summary>
/// <param name="Pace">Rhythm of the Working status; <see cref="WorkPace.Active"/> for every other status.</param>
public sealed record StatusSnapshot(ClaudeStatus Status, int ActiveSessions, DateTimeOffset UpdatedAt, WorkPace Pace = WorkPace.Active)
{
    public static StatusSnapshot Initial(DateTimeOffset now) => new(ClaudeStatus.Idle, 0, now);
}
