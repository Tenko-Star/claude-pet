namespace StatusHub.Service.Status;

public sealed class StatusOptions
{
    public const string SectionName = "Status";

    /// <summary>A session with no events for this long is dropped.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A subagent with no events for this long is dropped.</summary>
    public TimeSpan SubagentTimeout { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How often stale sessions and subagents are checked for.</summary>
    public TimeSpan ExpiryScanInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>A Working session with no new tool call for this long after its last tool ended shows Thinking again.</summary>
    public TimeSpan ThinkFallback { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>An Idle primary session hands over to an active session after this long.</summary>
    public TimeSpan PrimaryIdleRebind { get; set; } = TimeSpan.FromMinutes(3);
}
