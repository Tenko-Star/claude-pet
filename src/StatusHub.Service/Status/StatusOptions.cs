namespace StatusHub.Service.Status;

public sealed class StatusOptions
{
    public const string SectionName = "Status";

    /// <summary>A session with no events for this long is dropped.</summary>
    public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often stale sessions are checked for.</summary>
    public TimeSpan ExpiryScanInterval { get; set; } = TimeSpan.FromSeconds(30);
}
