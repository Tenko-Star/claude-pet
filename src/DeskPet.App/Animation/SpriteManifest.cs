namespace DeskPet.App.Animation;

/// <summary>Parsed <c>runtime/manifest.json</c>: canvas and stage geometry, shared layers, states and reactions.</summary>
/// <param name="Width">Character canvas width; every character sprite has this size.</param>
/// <param name="Height">Character canvas height.</param>
/// <param name="StageWidth">Width of the rendered image, which leaves room for effects.</param>
/// <param name="StageHeight">Height of the rendered image.</param>
/// <param name="CharacterOffset">Where the character canvas sits on the stage.</param>
/// <param name="Layers">Layer names bottom to top; each is one of <see cref="ManifestLayers"/>.</param>
public sealed record SpriteManifest(
    int Width,
    int Height,
    int StageWidth,
    int StageHeight,
    PixelPoint CharacterOffset,
    IReadOnlyList<string> Layers,
    HairSpec Hair,
    BlinkSpec Blink,
    MouthSpec Mouth,
    IReadOnlyDictionary<string, StateSpec> States,
    IReadOnlyDictionary<string, ReactionSpec> Reactions)
{
    /// <summary>Sprites drawn on the character canvas; they must all be canvas-sized.</summary>
    public IEnumerable<string> CharacterFiles =>
        Hair.Frames
            .Concat(Blink.Steps.Select(s => s.File))
            .Append(Mouth.OpenFile)
            .Concat(States.Values.SelectMany(s => s.CharacterFiles))
            .Distinct(StringComparer.Ordinal);

    /// <summary>Effect sprites placed on the stage; any size.</summary>
    public IEnumerable<string> EffectFiles =>
        States.Values.SelectMany(s => s.Fx?.Files ?? Enumerable.Empty<string>())
            .Concat(Reactions.Values.SelectMany(r => r.Frames.SelectMany(f => f.Sprites.Select(s => s.File))))
            .Distinct(StringComparer.Ordinal);

    /// <summary>Every sprite file referenced anywhere, without duplicates.</summary>
    public IEnumerable<string> AllFiles => CharacterFiles.Concat(EffectFiles).Distinct(StringComparer.Ordinal);
}

/// <summary>Layer names the manifest's <c>layers</c> array may contain.</summary>
public static class ManifestLayers
{
    public const string Hair = "hair";
    public const string Body = "body";
    public const string Eyes = "eyes";
    public const string Mouth = "mouth";
    public const string Fx = "fx";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Hair, Body, Eyes, Mouth, Fx };
}

/// <summary>A position in sprite pixels.</summary>
public readonly record struct PixelPoint(int X, int Y);

/// <summary>An inclusive range of durations; a random value inside it is picked each time.</summary>
public sealed record TimeRange(TimeSpan Min, TimeSpan Max);

/// <summary>Back hair frames, cycled forever.</summary>
public sealed record HairSpec(IReadOnlyList<string> Frames, TimeSpan FrameDuration);

/// <summary>A step of a blink sequence.</summary>
public sealed record BlinkStep(string File, TimeSpan Duration);

/// <summary>Blink sequence played at random intervals.</summary>
public sealed record BlinkSpec(IReadOnlyList<BlinkStep> Steps, TimeSpan MinInterval, TimeSpan MaxInterval)
{
    public TimeSpan SequenceDuration => Steps.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);
}

/// <summary>Mouth patch toggled on and off while talking.</summary>
public sealed record MouthSpec(string OpenFile, TimeSpan ToggleInterval);

/// <summary>A body image shown for a fixed time during an enter or exit transition.</summary>
public sealed record BodyFrame(string File, TimeSpan Duration);

public enum EyesMode
{
    /// <summary>Random blinks over the body's own eyes.</summary>
    Blink,

    /// <summary>No eye patch; the body carries its own expression.</summary>
    Off,

    /// <summary>A fixed eye patch (<see cref="StateSpec.EyesFile"/>) is always drawn.</summary>
    Fixed,
}

/// <summary>One character state, such as idle or working.</summary>
/// <param name="Enter">Body frames played before resting on <paramref name="Body"/>.</param>
/// <param name="Exit">Body frames played when leaving this state.</param>
/// <param name="Mouth">Whether the talk mouth patch may be drawn.</param>
/// <param name="Duration">When set, the state switches to <paramref name="Then"/> after this long.</param>
/// <param name="HairFrameDuration">Overrides <see cref="HairSpec.FrameDuration"/> while in this state.</param>
public sealed record StateSpec(
    string Name,
    string Body,
    IReadOnlyList<BodyFrame> Enter,
    IReadOnlyList<BodyFrame> Exit,
    EyesMode Eyes,
    string? EyesFile,
    bool Mouth,
    FxSpec? Fx,
    TapSpec? Tap,
    TimeSpan? Duration,
    string? Then,
    TimeSpan? HairFrameDuration)
{
    public IEnumerable<string> CharacterFiles
    {
        get
        {
            yield return Body;
            foreach (var frame in Enter.Concat(Exit))
            {
                yield return frame.File;
            }
            if (EyesFile is not null)
            {
                yield return EyesFile;
            }
            if (Tap is not null)
            {
                yield return Tap.Frame;
            }
        }
    }
}

/// <summary>
/// Working-state key presses: bursts of <see cref="Frame"/> shown for <see cref="Down"/>,
/// separated by <see cref="Gap"/> within a burst and <see cref="Pause"/> between bursts.
/// </summary>
public sealed record TapSpec(string Frame, TimeSpan Down, TimeRange Gap, int BurstMin, int BurstMax, TimeRange Pause);

/// <summary>An effect sprite; the left edge is at anchor.X + Dx and the bottom edge at anchor.Y + Dy.</summary>
public sealed record FxSprite(string File, int Dx, int Dy);

/// <summary>A set of effect sprites shown together for <see cref="Duration"/>. May be empty.</summary>
public sealed record FxFrame(TimeSpan Duration, IReadOnlyList<FxSprite> Sprites)
{
    public static TimeSpan TotalDuration(IReadOnlyList<FxFrame> frames) =>
        frames.Aggregate(TimeSpan.Zero, (sum, f) => sum + f.Duration);
}

/// <summary>Effect layer of a state, positioned relative to <see cref="Anchor"/> in stage coordinates.</summary>
public abstract record FxSpec(PixelPoint Anchor)
{
    public abstract IEnumerable<string> Files { get; }
}

/// <summary>Frames cycled forever.</summary>
public sealed record LoopFx(PixelPoint Anchor, IReadOnlyList<FxFrame> Frames) : FxSpec(Anchor)
{
    public override IEnumerable<string> Files => Frames.SelectMany(f => f.Sprites.Select(s => s.File));
}

/// <summary>
/// Pop frames played once, then <see cref="Hold"/> bobbing until the pop repeats
/// <see cref="RepeatInterval"/> after it ended.
/// </summary>
public sealed record PopFx(PixelPoint Anchor, IReadOnlyList<FxFrame> Pop, FxHold Hold, TimeSpan RepeatInterval) : FxSpec(Anchor)
{
    public override IEnumerable<string> Files => Pop.SelectMany(f => f.Sprites.Select(s => s.File)).Append(Hold.File);
}

/// <summary>Sprite held after a pop, moved by <see cref="BobPx"/> every other <see cref="BobInterval"/>.</summary>
public sealed record FxHold(string File, int BobPx, TimeSpan BobInterval);

/// <summary>Short overlay played once on top of the current state without changing it.</summary>
public sealed record ReactionSpec(string Name, PixelPoint Anchor, IReadOnlyList<FxFrame> Frames);
