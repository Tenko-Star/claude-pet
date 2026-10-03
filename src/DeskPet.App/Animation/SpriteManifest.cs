namespace DeskPet.App.Animation;

/// <summary>Parsed <c>runtime/manifest.json</c>: canvas size and layers bottom to top.</summary>
public sealed record SpriteManifest(int Width, int Height, IReadOnlyList<LayerSpec> Layers)
{
    /// <summary>Every sprite file referenced by any layer, without duplicates.</summary>
    public IEnumerable<string> AllFiles => Layers.SelectMany(l => l.Files).Distinct(StringComparer.Ordinal);
}

/// <summary>One layer of the manifest. The concrete kind is inferred from the JSON shape.</summary>
public abstract record LayerSpec(string Name)
{
    public abstract IEnumerable<string> Files { get; }
}

/// <summary>A layer that always shows the same image.</summary>
public sealed record StaticLayer(string Name, string File) : LayerSpec(Name)
{
    public override IEnumerable<string> Files => [File];
}

/// <summary>A layer that cycles through frames forever, each shown for <paramref name="FrameDuration"/>.</summary>
public sealed record LoopLayer(string Name, IReadOnlyList<string> Frames, TimeSpan FrameDuration) : LayerSpec(Name)
{
    public override IEnumerable<string> Files => Frames;
}

/// <summary>A step of a blink sequence.</summary>
public sealed record BlinkStep(string File, TimeSpan Duration);

/// <summary>A layer that is empty except while a blink sequence plays, at random intervals.</summary>
public sealed record BlinkLayer(string Name, IReadOnlyList<BlinkStep> Steps, TimeSpan MinInterval, TimeSpan MaxInterval)
    : LayerSpec(Name)
{
    public override IEnumerable<string> Files => Steps.Select(s => s.File);

    public TimeSpan SequenceDuration => Steps.Aggregate(TimeSpan.Zero, (sum, s) => sum + s.Duration);
}

/// <summary>A layer that toggles an "open" image on and off while talking.</summary>
public sealed record TalkLayer(string Name, string OpenFile, TimeSpan ToggleInterval) : LayerSpec(Name)
{
    public override IEnumerable<string> Files => [OpenFile];
}
