using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace StatusHub.Service.HookIngest;

/// <summary>Appends received hook requests to a JSONL file, one line per request.</summary>
public sealed class HookEventLog : IDisposable
{
    public const string FileName = "hook-events.jsonl";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Keep non-ASCII text (e.g. prompts) readable; the file is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // Serializes appends so concurrent requests never interleave lines.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HookEventLog(IOptions<HookIngestOptions> options, IHostEnvironment environment)
    {
        var directory = options.Value.DataDirectory;
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ClaudePet",
                "hooks");
        }
        else if (!Path.IsPathRooted(directory))
        {
            directory = Path.Combine(environment.ContentRootPath, directory);
        }

        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, FileName);
    }

    public string FilePath { get; }

    public async Task AppendAsync(
        DateTimeOffset receivedAt, string eventName, string body, CancellationToken cancellationToken)
    {
        var line = FormatLine(receivedAt, eventName, body);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await File.AppendAllTextAsync(FilePath, line, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// Builds one JSONL line. A valid JSON body is embedded as <c>payload</c> (compacted to one line);
    /// anything else is kept verbatim as the string <c>rawBody</c>.
    /// </summary>
    internal static string FormatLine(DateTimeOffset receivedAt, string eventName, string body)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("receivedAt", receivedAt);
            writer.WriteString("eventName", eventName);

            var payload = TryParse(body);
            if (payload is not null)
            {
                using (payload)
                {
                    writer.WritePropertyName("payload");
                    payload.RootElement.WriteTo(writer);
                }
            }
            else
            {
                writer.WriteString("rawBody", body);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    private static JsonDocument? TryParse(string body)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
