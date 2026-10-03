using System.IO;
using System.Text.Json;

namespace DeskPet.App.Animation;

/// <summary>
/// Parses the pixel-girl manifest: <c>canvas</c>, <c>stage</c>, <c>layers</c>, the shared
/// <c>hair</c>/<c>eyes</c>/<c>mouth</c> entries, <c>states</c> and optional <c>reactions</c>.
/// Purely descriptive keys (notes, hook names) are ignored. All errors are <see cref="InvalidDataException"/>.
/// </summary>
public static class ManifestParser
{
    public static SpriteManifest Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("manifest: the root must be an object.");
        }

        var (width, height) = PositivePair(Required(root, "canvas", "manifest"), "root", "canvas");
        var stage = Object(Required(root, "stage", "manifest"), "stage");
        var (stageWidth, stageHeight) = PositivePair(Required(stage, "size", "stage"), "stage", "size");
        var offset = Point(Required(stage, "characterOffset", "stage"), "stage", "characterOffset");
        if (offset.X < 0 || offset.Y < 0 || offset.X + width > stageWidth || offset.Y + height > stageHeight)
        {
            throw new InvalidDataException("manifest: the character canvas does not fit on the stage.");
        }

        var layers = ParseLayers(Required(root, "layers", "manifest"));
        var hair = ParseHair(Object(Required(root, "hair", "manifest"), "hair"));
        var blink = ParseBlink(Object(Required(root, "eyes", "manifest"), "eyes"));
        var mouth = ParseMouth(Object(Required(root, "mouth", "manifest"), "mouth"));

        var statesElement = Object(Required(root, "states", "manifest"), "states");
        var states = new Dictionary<string, StateSpec>(StringComparer.Ordinal);
        foreach (var property in statesElement.EnumerateObject())
        {
            states[property.Name] = ParseState(property.Name, Object(property.Value, $"state '{property.Name}'"));
        }
        if (states.Count == 0)
        {
            throw new InvalidDataException("manifest: 'states' must not be empty.");
        }
        foreach (var state in states.Values)
        {
            if (state.Then is not null && !states.ContainsKey(state.Then))
            {
                throw new InvalidDataException($"manifest: state '{state.Name}': 'then' refers to unknown state '{state.Then}'.");
            }
        }

        var reactions = new Dictionary<string, ReactionSpec>(StringComparer.Ordinal);
        if (root.TryGetProperty("reactions", out var reactionsElement))
        {
            foreach (var property in Object(reactionsElement, "reactions").EnumerateObject())
            {
                var owner = $"reaction '{property.Name}'";
                var entry = Object(property.Value, owner);
                reactions[property.Name] = new ReactionSpec(
                    property.Name,
                    Point(Required(entry, "anchor", owner), owner, "anchor"),
                    FxFrames(Required(entry, "frames", owner), owner, "frames"));
            }
        }

        return new SpriteManifest(width, height, stageWidth, stageHeight, offset, layers, hair, blink, mouth, states, reactions);
    }

    private static List<string> ParseLayers(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            throw new InvalidDataException("manifest: 'layers' must be a non-empty array.");
        }
        var layers = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            var name = item.ValueKind == JsonValueKind.String ? item.GetString()! : null;
            if (name is null || !ManifestLayers.All.Contains(name))
            {
                throw new InvalidDataException($"manifest: unknown layer '{item}'; expected one of {string.Join(", ", ManifestLayers.All)}.");
            }
            if (layers.Contains(name))
            {
                throw new InvalidDataException($"manifest: layer '{name}' is listed twice.");
            }
            layers.Add(name);
        }
        return layers;
    }

    private static HairSpec ParseHair(JsonElement entry)
    {
        var frames = Required(entry, "frames", "hair");
        if (frames.ValueKind != JsonValueKind.Array || frames.GetArrayLength() == 0)
        {
            throw new InvalidDataException("manifest: 'hair': 'frames' must be a non-empty array.");
        }
        return new HairSpec(
            frames.EnumerateArray().Select(f => FileName(f, "hair")).ToList(),
            Millis(Required(entry, "frameMs", "hair"), "hair", "frameMs"));
    }

    private static BlinkSpec ParseBlink(JsonElement entry)
    {
        var steps = BodyFrames(Required(entry, "blink", "eyes"), "eyes", "blink");
        if (steps.Count == 0)
        {
            throw new InvalidDataException("manifest: 'eyes': 'blink' must be a non-empty array.");
        }
        var interval = Range(Required(entry, "intervalMs", "eyes"), "eyes", "intervalMs");
        return new BlinkSpec(steps.Select(s => new BlinkStep(s.File, s.Duration)).ToList(), interval.Min, interval.Max);
    }

    private static MouthSpec ParseMouth(JsonElement entry) =>
        new(FileName(Required(entry, "open", "mouth"), "mouth"), Millis(Required(entry, "talkToggleMs", "mouth"), "mouth", "talkToggleMs"));

    private static StateSpec ParseState(string name, JsonElement entry)
    {
        var owner = $"state '{name}'";

        var eyes = EyesMode.Blink;
        string? eyesFile = null;
        if (entry.TryGetProperty("eyes", out var eyesElement))
        {
            var value = FileName(eyesElement, owner);
            eyes = value switch
            {
                "blink" => EyesMode.Blink,
                "off" => EyesMode.Off,
                _ => EyesMode.Fixed,
            };
            eyesFile = eyes == EyesMode.Fixed ? value : null;
        }

        var mouth = false;
        if (entry.TryGetProperty("mouth", out var mouthElement))
        {
            mouth = mouthElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new InvalidDataException($"manifest: {owner}: 'mouth' must be true or false."),
            };
        }

        TimeSpan? duration = entry.TryGetProperty("durationMs", out var durationElement)
            ? Millis(durationElement, owner, "durationMs")
            : null;
        string? then = entry.TryGetProperty("then", out var thenElement) ? FileName(thenElement, owner) : null;
        if (duration is not null && then is null)
        {
            throw new InvalidDataException($"manifest: {owner}: 'durationMs' needs 'then'.");
        }

        return new StateSpec(
            name,
            FileName(Required(entry, "body", owner), owner),
            entry.TryGetProperty("enter", out var enter) ? BodyFrames(enter, owner, "enter") : [],
            entry.TryGetProperty("exit", out var exit) ? BodyFrames(exit, owner, "exit") : [],
            eyes,
            eyesFile,
            mouth,
            entry.TryGetProperty("fx", out var fx) ? ParseFx(Object(fx, $"{owner} fx"), $"{owner} fx") : null,
            ParseTapWithPaces(entry, owner),
            duration,
            then,
            entry.TryGetProperty("hairFrameMs", out var hairMs) ? (TimeSpan?)Millis(hairMs, owner, "hairFrameMs") : null);
    }

    private static FxSpec ParseFx(JsonElement entry, string owner)
    {
        var anchor = Point(Required(entry, "anchor", owner), owner, "anchor");
        if (entry.TryGetProperty("loop", out var loop))
        {
            var frames = FxFrames(loop, owner, "loop");
            if (frames.Count == 0)
            {
                throw new InvalidDataException($"manifest: {owner}: 'loop' must be a non-empty array.");
            }
            return new LoopFx(anchor, frames);
        }
        if (entry.TryGetProperty("pop", out var pop))
        {
            var frames = FxFrames(pop, owner, "pop");
            var holdElement = Object(Required(entry, "hold", owner), $"{owner} hold");
            var bobPx = Required(holdElement, "bobPx", $"{owner} hold");
            var hold = new FxHold(
                FileName(Required(holdElement, "sprite", $"{owner} hold"), owner),
                Int(bobPx, owner, "bobPx"),
                Millis(Required(holdElement, "bobMs", $"{owner} hold"), owner, "bobMs"));
            return new PopFx(anchor, frames, hold, Millis(Required(entry, "repeatMs", owner), owner, "repeatMs"));
        }
        throw new InvalidDataException($"manifest: {owner} needs 'loop' or 'pop'.");
    }

    // "tap" plus the optional sibling "paces": { "<pace>": { "taps": [min, max], "pauseMs": [min, max] } }.
    private static TapSpec? ParseTapWithPaces(JsonElement state, string owner)
    {
        if (!state.TryGetProperty("tap", out var tapElement))
        {
            if (state.TryGetProperty("paces", out _))
            {
                throw new InvalidDataException($"manifest: {owner}: 'paces' needs 'tap'.");
            }
            return null;
        }

        var paces = new Dictionary<string, TapPace>(StringComparer.Ordinal);
        if (state.TryGetProperty("paces", out var pacesElement))
        {
            foreach (var pace in Object(pacesElement, $"{owner} paces").EnumerateObject())
            {
                var paceOwner = $"{owner} pace '{pace.Name}'";
                var paceEntry = Object(pace.Value, paceOwner);
                var (min, max) = CountRange(Required(paceEntry, "taps", paceOwner), paceOwner, "taps");
                paces[pace.Name] = new TapPace(min, max, Range(Required(paceEntry, "pauseMs", paceOwner), paceOwner, "pauseMs"));
            }
        }

        var tapOwner = $"{owner} tap";
        var tap = Object(tapElement, tapOwner);
        var (burstMin, burstMax) = CountRange(Required(tap, "burst", tapOwner), tapOwner, "burst");
        return new TapSpec(
            FileName(Required(tap, "frame", tapOwner), tapOwner),
            Millis(Required(tap, "downMs", tapOwner), tapOwner, "downMs"),
            Range(Required(tap, "gapMs", tapOwner), tapOwner, "gapMs"),
            burstMin,
            burstMax,
            Range(Required(tap, "pauseMs", tapOwner), tapOwner, "pauseMs"),
            paces);
    }

    // [min, max] with 1 <= min <= max.
    private static (int Min, int Max) CountRange(JsonElement element, string owner, string property)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' must be [min, max].");
        }
        var min = Int(element[0], owner, property);
        var max = Int(element[1], owner, property);
        if (min < 1 || max < min)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' must be positive with max not below min.");
        }
        return (min, max);
    }

    // [[file, ms], ...]
    private static List<BodyFrame> BodyFrames(JsonElement element, string owner, string property)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' must be an array.");
        }
        return element.EnumerateArray().Select(step =>
        {
            if (step.ValueKind != JsonValueKind.Array || step.GetArrayLength() != 2)
            {
                throw new InvalidDataException($"manifest: {owner}: '{property}' steps must be [file, ms].");
            }
            return new BodyFrame(FileName(step[0], owner), Millis(step[1], owner, property));
        }).ToList();
    }

    // [{ "ms": n, "sprites": [[file, dx, dy], ...] }, ...]
    private static List<FxFrame> FxFrames(JsonElement element, string owner, string property)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() == 0)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' must be a non-empty array.");
        }
        return element.EnumerateArray().Select(frame =>
        {
            var obj = Object(frame, owner);
            var sprites = Required(obj, "sprites", owner);
            if (sprites.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException($"manifest: {owner}: 'sprites' must be an array.");
            }
            return new FxFrame(
                Millis(Required(obj, "ms", owner), owner, "ms"),
                sprites.EnumerateArray().Select(s =>
                {
                    if (s.ValueKind != JsonValueKind.Array || s.GetArrayLength() != 3)
                    {
                        throw new InvalidDataException($"manifest: {owner}: sprites must be [file, dx, dy].");
                    }
                    return new FxSprite(FileName(s[0], owner), Int(s[1], owner, "dx"), Int(s[2], owner, "dy"));
                }).ToList());
        }).ToList();
    }

    private static JsonElement Required(JsonElement obj, string property, string owner) =>
        obj.TryGetProperty(property, out var value)
            ? value
            : throw new InvalidDataException($"manifest: '{owner}' is missing '{property}'.");

    private static JsonElement Object(JsonElement element, string owner) =>
        element.ValueKind == JsonValueKind.Object
            ? element
            : throw new InvalidDataException($"manifest: {owner} must be an object.");

    private static string FileName(JsonElement element, string owner) =>
        element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString())
            ? element.GetString()!
            : throw new InvalidDataException($"manifest: {owner} has an invalid file name.");

    private static TimeSpan Millis(JsonElement element, string owner, string property) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(ms)
            : throw new InvalidDataException($"manifest: {owner}: '{property}' must be a positive integer.");

    private static int Int(JsonElement element, string owner, string property) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : throw new InvalidDataException($"manifest: {owner}: '{property}' must be an integer.");

    private static TimeRange Range(JsonElement element, string owner, string property)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' must be [min, max].");
        }
        var min = Millis(element[0], owner, property);
        var max = Millis(element[1], owner, property);
        if (max < min)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' max is below min.");
        }
        return new TimeRange(min, max);
    }

    private static PixelPoint Point(JsonElement element, string owner, string property)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' must be [x, y].");
        }
        return new PixelPoint(Int(element[0], owner, property), Int(element[1], owner, property));
    }

    private static (int, int) PositivePair(JsonElement element, string owner, string property)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' must be [width, height].");
        }
        var first = Int(element[0], owner, property);
        var second = Int(element[1], owner, property);
        if (first <= 0 || second <= 0)
        {
            throw new InvalidDataException($"manifest: {owner}: '{property}' must be positive.");
        }
        return (first, second);
    }
}
