namespace StatusHub.Service.HookIngest;

public sealed class HookIngestOptions
{
    public const string SectionName = "HookIngest";

    /// <summary>Loopback port for the hook endpoint. 0 picks a free port.</summary>
    public int Port { get; set; } = 47821;

    /// <summary>
    /// Directory for the JSONL log. Relative paths resolve against the content root.
    /// Empty means %LOCALAPPDATA%/ClaudePet/hooks.
    /// </summary>
    public string DataDirectory { get; set; } = "";
}
