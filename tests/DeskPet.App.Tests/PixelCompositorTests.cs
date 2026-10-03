using DeskPet.App.Animation;

namespace DeskPet.App.Tests;

public class PixelCompositorTests
{
    private static PixelBuffer Solid(int w, int h, params byte[][] pixels)
    {
        var buffer = PixelBuffer.Empty(w, h);
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i].CopyTo(buffer.Pbgra, i * 4);
        }
        return buffer;
    }

    [Fact]
    public void Transparent_pixels_keep_lower_layer_and_opaque_pixels_replace_it()
    {
        var bottom = Solid(2, 1, [10, 20, 30, 255], [40, 50, 60, 255]);
        var top = Solid(2, 1, [0, 0, 0, 0], [1, 2, 3, 255]);

        var result = PixelCompositor.Compose(2, 1, [bottom, top]);

        Assert.Equal(new byte[] { 10, 20, 30, 255, 1, 2, 3, 255 }, result.Pbgra);
    }

    [Fact]
    public void Partial_alpha_blends_source_over_in_premultiplied_space()
    {
        var bottom = Solid(1, 1, [0, 0, 200, 255]);
        var top = Solid(1, 1, [64, 0, 0, 128]);

        var result = PixelCompositor.Compose(1, 1, [bottom, top]);

        Assert.Equal(new byte[] { 64, 0, 100, 255 }, result.Pbgra);
    }

    [Fact]
    public void Compose_rejects_mismatched_layer_size()
    {
        Assert.Throws<ArgumentException>(() => PixelCompositor.Compose(2, 2, [PixelBuffer.Empty(1, 1)]));
    }

    [Fact]
    public void ScaleNearest_copies_each_pixel_into_an_exact_block()
    {
        byte[] a = [1, 2, 3, 255], b = [4, 5, 6, 128], c = [0, 0, 0, 0], d = [7, 8, 9, 255];
        var source = Solid(2, 2, a, b, c, d);

        var scaled = PixelCompositor.ScaleNearest(source, 3);

        Assert.Equal(6, scaled.Width);
        Assert.Equal(6, scaled.Height);
        for (var y = 0; y < 6; y++)
        {
            for (var x = 0; x < 6; x++)
            {
                var expected = (y / 3, x / 3) switch { (0, 0) => a, (0, 1) => b, (1, 0) => c, _ => d };
                Assert.Equal(expected, scaled.Pbgra.AsSpan((y * 6 + x) * 4, 4).ToArray());
            }
        }
    }

    [Fact]
    public void ScaleNearest_introduces_no_new_colors()
    {
        var source = PixelBuffer.Empty(5, 4);
        new Random(42).NextBytes(source.Pbgra);
        var scaled = PixelCompositor.ScaleNearest(source, 4);

        static HashSet<uint> Colors(PixelBuffer p) =>
            [.. Enumerable.Range(0, p.Width * p.Height).Select(i => BitConverter.ToUInt32(p.Pbgra, i * 4))];

        Assert.True(Colors(scaled).SetEquals(Colors(source)));
    }

    [Fact]
    public void ScaleNearest_rejects_non_positive_factor()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PixelCompositor.ScaleNearest(PixelBuffer.Empty(1, 1), 0));
    }
}
