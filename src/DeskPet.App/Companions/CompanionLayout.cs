using DeskPet.App.Animation;

namespace DeskPet.App.Companions;

/// <summary>Distances from a companion's body center to its outermost opaque pixel over all frames.</summary>
public readonly record struct Extent(int Left, int Right, int Top, int Bottom);

/// <summary>
/// Where companions go around the pet, in pet-stage pixels (the manifest stage). Oranges stand beside the pet's
/// body, left-low, right-low, left-high; orbs sit on shallow arcs above the head, eight per arc.
/// </summary>
/// <param name="Body">Inclusive bounds of every character layer the pet can show, including the hair swing.</param>
/// <param name="HeadTop">Middle of the topmost opaque row.</param>
public sealed record CompanionLayout(PixelRect Body, PixelPoint HeadTop)
{
    public const int MaxOranges = 3;
    public const int MaxOrbsPerOrange = 8;

    /// <summary>Body centers inside the frames (32x32 orange, 24x24 orb) and their extents.</summary>
    public static readonly PixelPoint OrangeCenter = new(17, 20);
    public static readonly PixelPoint OrbCenter = new(12, 12);
    public static readonly Extent OrangeExtent = new(12, 11, 12, 9);
    public static readonly Extent OrbExtent = new(7, 6, 7, 6);

    private const int OrangeGap = 8; // free pixels between an orange and the body
    private const double ShoulderAt = 0.49; // orange heights as a fraction of the body height
    private const double WaistAt = 0.70;
    private const int ArcGap = 6; // free pixels between the head top and the middle of the first arc
    private const int ArcPerRow = 8;
    private const int ArcStepX = 20;
    private const int ArcRowStep = 20;
    private const int ArcSag = 10; // the ends of a full arc sit this much lower than its middle
    private const int Margin = 12; // room for hops, sparkles and drifting dissolve particles

    /// <summary>Measures the pet from its character layers placed at <paramref name="offset"/> on the stage.</summary>
    public static CompanionLayout Measure(IEnumerable<PixelBuffer> characterLayers, PixelPoint offset)
    {
        int left = int.MaxValue, right = int.MinValue, top = int.MaxValue, bottom = int.MinValue;
        int headLeft = 0, headRight = 0;
        foreach (var layer in characterLayers)
        {
            for (var y = 0; y < layer.Height; y++)
            {
                for (var x = 0; x < layer.Width; x++)
                {
                    if (layer.Pbgra[(y * layer.Width + x) * 4 + 3] == 0)
                    {
                        continue;
                    }
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                    if (y < top)
                    {
                        (top, headLeft, headRight) = (y, x, x);
                    }
                    else if (y == top)
                    {
                        headLeft = Math.Min(headLeft, x);
                        headRight = Math.Max(headRight, x);
                    }
                }
            }
        }
        if (left > right)
        {
            throw new ArgumentException("The character layers have no opaque pixel.", nameof(characterLayers));
        }
        return new CompanionLayout(
            new PixelRect(left + offset.X, top + offset.Y, right + offset.X, bottom + offset.Y),
            new PixelPoint((headLeft + headRight) / 2 + offset.X, top + offset.Y));
    }

    /// <summary>Fixed center of the orange in slot <paramref name="index"/> (0..2).</summary>
    public PixelPoint OrangeSlot(int index)
    {
        var x = index % 2 == 0
            ? Body.Left - OrangeGap - 1 - OrangeExtent.Right
            : Body.Right + OrangeGap + 1 + OrangeExtent.Left;
        var at = index < 2 ? WaistAt : ShoulderAt;
        return new PixelPoint(x, Body.Top + (int)Math.Round((Body.Bottom - Body.Top) * at));
    }

    /// <summary>Centers of <paramref name="count"/> orbs, lowest arc first, left to right.</summary>
    public IReadOnlyList<PixelPoint> ArcSlots(int count)
    {
        var slots = new PixelPoint[count];
        var halfSpan = (ArcPerRow - 1) / 2.0 * ArcStepX;
        var baseY = HeadTop.Y - ArcGap - 1 - OrbExtent.Bottom;
        for (var k = 0; k < count; k++)
        {
            var row = k / ArcPerRow;
            var n = Math.Min(ArcPerRow, count - row * ArcPerRow);
            var dx = (k % ArcPerRow - (n - 1) / 2.0) * ArcStepX;
            var t = dx / halfSpan;
            slots[k] = new PixelPoint(
                (int)Math.Round(HeadTop.X + dx),
                (int)Math.Round(baseY - row * ArcRowStep + ArcSag * t * t));
        }
        return slots;
    }

    /// <summary>
    /// Room the companions need outside a stage of <paramref name="stageWidth"/> x <paramref name="stageHeight"/>,
    /// with every slot taken.
    /// </summary>
    public PixelRect Padding(int stageWidth, int stageHeight)
    {
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        for (var i = 0; i < MaxOranges; i++)
        {
            var c = OrangeSlot(i);
            left = Math.Min(left, c.X - OrangeExtent.Left);
            right = Math.Max(right, c.X + OrangeExtent.Right);
            top = Math.Min(top, c.Y - OrangeExtent.Top);
            bottom = Math.Max(bottom, c.Y + OrangeExtent.Bottom);
        }
        // One orb group per orange plus the primary session's group.
        foreach (var c in ArcSlots((MaxOranges + 1) * MaxOrbsPerOrange))
        {
            left = Math.Min(left, c.X - OrbExtent.Left);
            right = Math.Max(right, c.X + OrbExtent.Right);
            top = Math.Min(top, c.Y - OrbExtent.Top);
        }
        return new PixelRect(
            Math.Max(0, Margin - left), Math.Max(0, Margin - top),
            Math.Max(0, right + Margin - (stageWidth - 1)), Math.Max(0, bottom + Margin - (stageHeight - 1)));
    }
}

/// <summary>Inclusive pixel rectangle; also used for padding on each side.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom);
