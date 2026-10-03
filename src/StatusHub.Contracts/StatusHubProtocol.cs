namespace StatusHub.Contracts;

/// <summary>SignalR hub path and client method names.</summary>
public static class StatusHubProtocol
{
    public const string Path = "/hubs/status";

    /// <summary>Carries a <see cref="StatusSnapshot"/>. Sent on connect and whenever the status changes.</summary>
    public const string SnapshotMethod = "Snapshot";

    /// <summary>Carries a <see cref="StatusEvent"/>.</summary>
    public const string EventMethod = "Event";
}
