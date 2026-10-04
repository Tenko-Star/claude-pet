using System.Text.Json;

namespace StatusHub.Service.Status;

/// <summary>The part of a hook request the reducer needs.</summary>
/// <param name="AgentId">Set on events fired inside a subagent; null on main-thread events.</param>
/// <param name="AgentType">The subagent's type; null for main-thread events and for Claude Code's internal agents.</param>
public sealed record HookEvent(
    string EventName,
    string SessionId,
    DateTimeOffset ReceivedAt,
    string? NotificationType = null,
    string? AgentId = null,
    string? AgentType = null)
{
    /// <summary>Returns null when the body is not JSON or has no non-empty string <c>session_id</c>.</summary>
    public static HookEvent? TryParse(string eventName, string body, DateTimeOffset receivedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("session_id", out var id)
                && id.ValueKind == JsonValueKind.String
                && id.GetString() is { Length: > 0 } sessionId)
            {
                var notificationType = document.RootElement.TryGetProperty("notification_type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    ? type.GetString()
                    : null;
                var agentId = document.RootElement.TryGetProperty("agent_id", out var agent)
                    && agent.ValueKind == JsonValueKind.String
                    && agent.GetString() is { Length: > 0 } value
                    ? value
                    : null;
                var agentType = document.RootElement.TryGetProperty("agent_type", out var kind)
                    && kind.ValueKind == JsonValueKind.String
                    && kind.GetString() is { Length: > 0 } typeName
                    ? typeName
                    : null;
                return new HookEvent(eventName, sessionId, receivedAt, notificationType, agentId, agentType);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
