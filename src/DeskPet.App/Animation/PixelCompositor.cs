namespace DeskPet.App.Animation;

/// <summary>A tightly packed image in premultiplied BGRA (4 bytes per pixel, stride = width * 4).</summary>
public sealed record PixelBuffer(int Width, int Height, byte[] Pbgra)
{
    public int Stride => Width * 4;

    public static PixelBuffer Empty(int width, int height) => new(width, height, new byte[width * height * 4]);
}

/// <summary>Layer compositing and integer nearest-neighbor scaling on raw pixel buffers.</summary>
public static class PixelCompositor
{
    /// <summary>Draws <paramref name="layers"/> bottom to top with source-over blending.</summary>
    public static PixelBuffer Compose(int width, int height, IEnumerable<PixelBuffer> layers)
    {
        var result = PixelBuffer.Empty(width, height);
        var dst = result.Pbgra;
        foreach (var layer in layers)
        {
            if (layer.Width != width || layer.Height != height)
            {
                throw new ArgumentException($"Layer is {layer.Width}x{layer.Height}, expected {width}x{height}.", nameof(layers));
            }
            var src = layer.Pbgra;
            for (var i = 0; i < dst.Length; i += 4)
            {
                var a = src[i + 3];
                if (a == 0)
                {
                    continue;
                }
                if (a == 255)
                {
                    dst[i] = src[i];
                    dst[i + 1] = src[i + 1];
                    dst[i + 2] = src[i + 2];
                    dst[i + 3] = 255;
                    continue;
                }
                var inv = 255 - a;
                for (var c = 0; c < 4; c++)
                {
                    dst[i + c] = (byte)(src[i + c] + (dst[i + c] * inv + 127) / 255);
                }
            }
        }
        return result;
    }

    /// <summary>Scales by an integer factor, copying each source pixel into a factor x factor block.</summary>
    public static PixelBuffer ScaleNearest(PixelBuffer source, int factor)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(factor, 1);
        if (factor == 1)
        {
            return source;
        }

        var result = PixelBuffer.Empty(source.Width * factor, source.Height * factor);
        var dst = result.Pbgra;
        var dstStride = result.Stride;
        for (var y = 0; y < source.Height; y++)
        {
            var dstRow = y * factor * dstStride;
            for (var x = 0; x < source.Width; x++)
            {
                var pixel = source.Pbgra.AsSpan((y * source.Width + x) * 4, 4);
                for (var dx = 0; dx < factor; dx++)
                {
                    pixel.CopyTo(dst.AsSpan(dstRow + (x * factor + dx) * 4, 4));
                }
            }
            // Repeat the finished row for the remaining rows of this block.
            var firstRow = dst.AsSpan(dstRow, dstStride);
            for (var dy = 1; dy < factor; dy++)
            {
                firstRow.CopyTo(dst.AsSpan(dstRow + dy * dstStride, dstStride));
            }
        }
        return result;
    }
}
