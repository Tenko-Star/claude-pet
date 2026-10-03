namespace StatusHub.Contracts;

/// <summary>
/// One-shot moment such as a finished or failed turn or a failed tool call. <see cref="Kind"/> is Done, Error or ToolFailure.
/// </summary>
public sealed record StatusEvent(ClaudeStatus Kind, string SessionId, DateTimeOffset At);
