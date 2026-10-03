using System.Text.Json.Serialization;

namespace StatusHub.Contracts;

/// <summary>Rhythm of <see cref="ClaudeStatus.Working"/>. Only meaningful while the status is Working.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkPace>))]
public enum WorkPace
{
    /// <summary>A tool is executing (between PreToolUse and PostToolUse).</summary>
    Active,

    /// <summary>Between tool calls; the model is generating the next call.</summary>
    Composing,
}
