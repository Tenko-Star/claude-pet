namespace DeskPet.App;

/// <summary>Configuration bound from the <c>DeskPet</c> section of appsettings.json.</summary>
public sealed class DeskPetOptions
{
    public const string SectionName = "DeskPet";

    /// <summary>Default integer scale in device pixels per sprite pixel. A saved scale overrides it.</summary>
    public int Scale { get; set; } = 3;

    /// <summary>How long the pet stays idle before it falls asleep.</summary>
    public TimeSpan SleepAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>SignalR hub of StatusHub.Service; the port must match the service's HookIngest:Port.</summary>
    public string HubUrl { get; set; } = "http://127.0.0.1:47821/hubs/status";
}
