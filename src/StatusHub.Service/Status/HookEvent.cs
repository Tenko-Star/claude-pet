using System.Text.Json;

namespace StatusHub.Service.Status;

/// <summary>The part of a hook request the reducer needs.</summary>
public sealed record HookEvent(string EventName, string SessionId, DateTimeOffset ReceivedAt)
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
                return new HookEvent(eventName, sessionId, receivedAt);
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }
}
