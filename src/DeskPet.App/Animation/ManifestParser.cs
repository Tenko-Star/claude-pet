using System.IO;
using System.Text.Json;

namespace DeskPet.App.Animation;

/// <summary>
/// Parses the pixel-girl manifest. Layer names come from the <c>layers</c> array; each layer's
/// kind is inferred from the shape of its entry:
/// string = static, <c>frames</c> = loop, <c>blink</c> = blink, <c>open</c> = talk toggle.
/// </summary>
public static class ManifestParser
{
    public static SpriteManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var canvas = Required(root, "canvas", "manifest");
        if (canvas.ValueKind != JsonValueKind.Array || canvas.GetArrayLength() != 2)
        {
            throw new InvalidDataException("manifest: 'canvas' must be [width, height].");
        }
        var width = PositiveInt(canvas[0], "canvas width");
        var height = PositiveInt(canvas[1], "canvas height");

        var layerNames = Required(root, "layers", "manifest");
        if (layerNames.ValueKind != JsonValueKind.Array || layerNames.GetArrayLength() == 0)
        {
            throw new InvalidDataException("manifest: 'layers' must be a non-empty array.");
        }

        var layers = new List<LayerSpec>();
        foreach (var nameElement in layerNames.EnumerateArray())
        {
            var name = nameElement.GetString()
                ?? throw new InvalidDataException("manifest: layer names must be strings.");
            if (!root.TryGetProperty(name, out var entry))
            {
                throw new InvalidDataException($"manifest: layer '{name}' is listed but has no entry.");
            }
            layers.Add(ParseLayer(name, entry));
        }

        return new SpriteManifest(width, height, layers);
    }

    private static LayerSpec ParseLayer(string name, JsonElement entry)
    {
        if (entry.ValueKind == JsonValueKind.String)
        {
            return new StaticLayer(name, FileName(entry, name));
        }
        if (entry.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"manifest: layer '{name}' must be a file name or an object.");
        }

        if (entry.TryGetProperty("frames", out var frames))
        {
            var list = FileList(frames, name, "frames");
            return new LoopLayer(name, list, Millis(Required(entry, "frameMs", name), name, "frameMs"));
        }

        if (entry.TryGetProperty("blink", out var blink))
        {
            if (blink.ValueKind != JsonValueKind.Array || blink.GetArrayLength() == 0)
            {
                throw new InvalidDataException($"manifest: layer '{name}': 'blink' must be a non-empty array.");
            }
            var steps = blink.EnumerateArray().Select(step =>
            {
                if (step.ValueKind != JsonValueKind.Array || step.GetArrayLength() != 2)
                {
                    throw new InvalidDataException($"manifest: layer '{name}': blink steps must be [file, ms].");
                }
                return new BlinkStep(FileName(step[0], name), Millis(step[1], name, "blink"));
            }).ToList();

            var interval = Required(entry, "intervalMs", name);
            if (interval.ValueKind != JsonValueKind.Array || interval.GetArrayLength() != 2)
            {
                throw new InvalidDataException($"manifest: layer '{name}': 'intervalMs' must be [min, max].");
            }
            var min = Millis(interval[0], name, "intervalMs");
            var max = Millis(interval[1], name, "intervalMs");
            if (max < min)
            {
                throw new InvalidDataException($"manifest: layer '{name}': 'intervalMs' max is below min.");
            }
            return new BlinkLayer(name, steps, min, max);
        }

        if (entry.TryGetProperty("open", out var open))
        {
            return new TalkLayer(name, FileName(open, name), Millis(Required(entry, "talkToggleMs", name), name, "talkToggleMs"));
        }

        throw new InvalidDataException($"manifest: layer '{name}' has an unrecognized shape.");
    }

    private static JsonElement Required(JsonElement obj, string property, string owner) =>
        obj.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidDataException($"manifest: '{owner}' is missing '{property}'.");

    private static string FileName(JsonElement element, string layer) =>
        element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new InvalidDataException($"manifest: layer '{layer}' has an invalid file name.");

    private static List<string> FileList(JsonElement element, string layer, string property)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"manifest: layer '{layer}': '{property}' must be a non-empty array.");
        }
        return element.EnumerateArray().Select(e => FileName(e, layer)).ToList();
    }

    private static TimeSpan Millis(JsonElement element, string layer, string property) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : throw new InvalidDataException($"manifest: layer '{layer}': '{property}' must be a positive integer.");

    private static int PositiveInt(JsonElement element, string what) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value) && value > 0
            ? value
            : throw new InvalidDataException($"manifest: {what} must be a positive integer.");
}
