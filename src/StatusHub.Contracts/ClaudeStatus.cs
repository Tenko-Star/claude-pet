using System.Text.Json.Serialization;

namespace StatusHub.Contracts;

/// <summary>
/// Status of Claude Code as shown to clients. Snapshots only carry Idle, Thinking, Working and Waiting;
/// Done, Error and ToolFailure are one-shot <see cref="StatusEvent"/> kinds.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClaudeStatus>))]
public enum ClaudeStatus
{
    Idle,
    Thinking,
    Working,
    Waiting,
    Done,
    Error,
    ToolFailure,
}
