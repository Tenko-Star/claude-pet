using System.IO;
using DeskPet.App.Animation;
using DeskPet.App.Rendering;

namespace DeskPet.App.Companions;

/// <summary>Frame indices shared by both companion types, in file order; the orb has no half blink.</summary>
public enum CompanionFrame
{
    Base = 0,
    Squash = 1,
    Stretch = 2,
    Happy = 3,
    Error = 4,
    Blink = 5,
    BlinkHalf = 6,
}

/// <summary>
/// Decoded companion frames. Oranges keep their colors; orbs get one palette variant per orange slot
/// (original, blue, green, purple) so an orb shows which orange it belongs to.
/// </summary>
/// <param name="Orange">Orange frames indexed by <see cref="CompanionFrame"/>.</param>
/// <param name="Orbs">Orb frames per variant, indexed by <see cref="CompanionFrame"/> up to Blink.</param>
/// <param name="OrbBright">Per variant: 1x1 lightest body color, the spawn pixel.</param>
/// <param name="OrbSparkle">Per variant: 1x1 second-lightest body color, the sparkles.</param>
public sealed record CompanionArt(
    IReadOnlyList<PixelBuffer> Orange,
    IReadOnlyList<IReadOnlyList<PixelBuffer>> Orbs,
    IReadOnlyList<PixelBuffer> OrbBright,
    IReadOnlyList<PixelBuffer> OrbSparkle)
{
    /// <summary>Folder next to the executable that holds the orange/ and orb/ frames.</summary>
    public const string DirectoryName = "companions";

    private static readonly string[] OrangeFiles =
    [
        "orange_00_base.png", "orange_01_squash.png", "orange_02_stretch.png", "orange_03_face_happy.png",
        "orange_04_face_error.png", "orange_05_blink.png", "orange_06_blink_half.png",
    ];

    private static readonly string[] OrbFiles =
    [
        "orb_00_base.png", "orb_01_squash.png", "orb_02_stretch.png", "orb_03_face_happy.png",
        "orb_04_face_error.png", "orb_05_blink.png",
    ];

    // Target hue per orb variant; null keeps the original colors.
    private static readonly double?[] HueTargets = [null, 210, 125, 275];

    // Never recolored: the eye color and the white highlight.
    private static readonly int[] ProtectedColors = [Rgb(72, 40, 14), Rgb(255, 255, 255)];

    public static CompanionArt Load(string directory)
    {
        var orange = OrangeFiles.Select(f => SpriteLibrary.Decode(Path.Combine(directory, "orange", f))).ToArray();
        var orb = OrbFiles.Select(f => SpriteLibrary.Decode(Path.Combine(directory, "orb", f))).ToArray();
        return FromFrames(orange, orb);
    }

    /// <summary>Builds the orb variants from opaque frames in original colors.</summary>
    public static CompanionArt FromFrames(IReadOnlyList<PixelBuffer> orange, IReadOnlyList<PixelBuffer> orb)
    {
        // Pixel counts per recolorable color over all orb frames.
        var counts = new Dictionary<int, int>();
        foreach (var frame in orb)
        {
            for (var i = 0; i < frame.Pbgra.Length; i += 4)
            {
                if (frame.Pbgra[i + 3] == 255 && Read(frame.Pbgra, i) is var color && !ProtectedColors.Contains(color))
                {
                    counts[color] = counts.GetValueOrDefault(color) + 1;
                }
            }
        }
        var dominant = DominantHue(counts);
        var byLight = counts.Keys.OrderByDescending(c => ToHsl(c).L).ToArray();

        var variants = new List<IReadOnlyList<PixelBuffer>>();
        var bright = new List<PixelBuffer>();
        var sparkle = new List<PixelBuffer>();
        foreach (var target in HueTargets)
        {
            var map = counts.Keys.ToDictionary(c => c, c => target is { } hue ? Shift(c, hue - dominant) : c);
            variants.Add(orb.Select(frame => Recolor(frame, map)).ToArray());
            bright.Add(Pixel(map[byLight[0]]));
            sparkle.Add(Pixel(map[byLight[Math.Min(1, byLight.Length - 1)]]));
        }
        return new CompanionArt(orange, variants, bright, sparkle);
    }

    /// <summary>Gray with the same lightness, as a packed 0xRRGGBB color; for dissolve particles of an errored companion.</summary>
    public static int Gray(int color)
    {
        var l = (int)Math.Round(ToHsl(color).L * 255);
        return Rgb(l, l, l);
    }

    /// <summary>Packed 0xRRGGBB color of an opaque pixel at byte offset <paramref name="i"/>.</summary>
    public static int Read(byte[] pbgra, int i) => Rgb(pbgra[i + 2], pbgra[i + 1], pbgra[i]);

    private static int Rgb(int r, int g, int b) => (r << 16) | (g << 8) | b;

    private static PixelBuffer Pixel(int color)
    {
        var buffer = PixelBuffer.Empty(1, 1);
        Write(buffer.Pbgra, 0, color);
        return buffer;
    }

    private static void Write(byte[] pbgra, int i, int color)
    {
        pbgra[i] = (byte)color;
        pbgra[i + 1] = (byte)(color >> 8);
        pbgra[i + 2] = (byte)(color >> 16);
        pbgra[i + 3] = 255;
    }

    private static PixelBuffer Recolor(PixelBuffer frame, Dictionary<int, int> map)
    {
        var result = new PixelBuffer(frame.Width, frame.Height, (byte[])frame.Pbgra.Clone());
        for (var i = 0; i < result.Pbgra.Length; i += 4)
        {
            if (result.Pbgra[i + 3] == 255 && map.TryGetValue(Read(result.Pbgra, i), out var color))
            {
                Write(result.Pbgra, i, color);
            }
        }
        return result;
    }

    // Saturation-weighted circular mean hue, weighted by pixel count.
    private static double DominantHue(Dictionary<int, int> counts)
    {
        double sx = 0, sy = 0;
        foreach (var (color, count) in counts)
        {
            var (h, s, _) = ToHsl(color);
            sx += count * s * Math.Cos(h * Math.PI / 180);
            sy += count * s * Math.Sin(h * Math.PI / 180);
        }
        return (Math.Atan2(sy, sx) * 180 / Math.PI + 360) % 360;
    }

    // Hue shift keeps saturation and lightness, so the lightness order of the palette stays the same.
    private static int Shift(int color, double degrees)
    {
        var (h, s, l) = ToHsl(color);
        return FromHsl(((h + degrees) % 360 + 360) % 360, s, l);
    }

    private static (double H, double S, double L) ToHsl(int color)
    {
        var r = ((color >> 16) & 255) / 255.0;
        var g = ((color >> 8) & 255) / 255.0;
        var b = (color & 255) / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min)
        {
            return (0, 0, l);
        }
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static int FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var hp = h / 60;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        var (r, g, b) = hp switch
        {
            < 1 => (c, x, 0.0),
            < 2 => (x, c, 0.0),
            < 3 => (0.0, c, x),
            < 4 => (0.0, x, c),
            < 5 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        var m = l - c / 2;
        return Rgb((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}
