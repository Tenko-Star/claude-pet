namespace StatusHub.Contracts;

/// <summary>One-shot moment such as a finished or failed turn. <see cref="Kind"/> is Done or Error.</summary>
public sealed record StatusEvent(ClaudeStatus Kind, string SessionId, DateTimeOffset At);
