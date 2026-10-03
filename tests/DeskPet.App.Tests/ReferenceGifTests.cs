using System.Windows.Media.Imaging;
using DeskPet.App.Animation;
using DeskPet.App.Rendering;

namespace DeskPet.App.Tests;

/// <summary>
/// Renders the idle state at each frame start of runtime/pixel-idle.gif and compares pixels.
/// The GIF loops 3200 ms with one blink starting at 1200 ms; it covers the character canvas,
/// so its frames are placed at the manifest's character offset on the stage.
/// </summary>
public class ReferenceGifTests
{
    private const int GifBlinkStartMs = 1200;

    [Fact]
    public void Player_output_matches_reference_gif_frames()
    {
        var library = SpriteLibrary.Load(TestAssets.RuntimeDirectory);
        var player = new SpritePlayer(library.Manifest, new RecordingLogger<SpritePlayer>(), (_, _) => TimeSpan.FromMilliseconds(GifBlinkStartMs));
        var gifFrames = DecodeGif(Path.Combine(TestAssets.RuntimeDirectory, "pixel-idle.gif"), library.Manifest);

        Assert.Equal(8, gifFrames.Count);
        Assert.Equal(3200, gifFrames.Sum(f => f.DurationMs));

        var start = 0;
        foreach (var (expected, durationMs) in gifFrames)
        {
            var actual = library.Compose(player.Evaluate(TimeSpan.FromMilliseconds(start)).Sprites);
            AssertSamePixels(expected, actual, start);
            start += durationMs;
        }
    }

    private static void AssertSamePixels(PixelBuffer expected, PixelBuffer actual, int atMs)
    {
        var mismatches = 0;
        for (var i = 0; i < expected.Pbgra.Length; i += 4)
        {
            var expectedOpaque = expected.Pbgra[i + 3] != 0;
            var actualOpaque = actual.Pbgra[i + 3] != 0;
            if (expectedOpaque != actualOpaque
                || (expectedOpaque && !expected.Pbgra.AsSpan(i, 3).SequenceEqual(actual.Pbgra.AsSpan(i, 3))))
            {
                mismatches++;
            }
        }
        Assert.True(mismatches == 0, $"{mismatches} pixels differ from the GIF frame at {atMs} ms.");
    }

    // Every frame in this GIF uses disposal "restore to background", so each frame is its
    // sub-rectangle placed on a transparent stage.
    private static List<(PixelBuffer Pixels, int DurationMs)> DecodeGif(string path, SpriteManifest manifest)
    {
        var width = manifest.StageWidth;
        var offset = manifest.CharacterOffset;
        using var stream = File.OpenRead(path);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frames = new List<(PixelBuffer, int)>();
        foreach (var frame in decoder.Frames)
        {
            var metadata = (BitmapMetadata)frame.Metadata;
            var left = Convert.ToInt32(metadata.GetQuery("/imgdesc/Left"));
            var top = Convert.ToInt32(metadata.GetQuery("/imgdesc/Top"));
            var disposal = Convert.ToInt32(metadata.GetQuery("/grctlext/Disposal"));
            var delayMs = Convert.ToInt32(metadata.GetQuery("/grctlext/Delay")) * 10;
            Assert.Equal(2, disposal);

            var part = SpriteLibrary.ToPixelBuffer(frame);
            var canvas = PixelBuffer.Empty(width, manifest.StageHeight);
            for (var y = 0; y < part.Height; y++)
            {
                var row = (offset.Y + top + y) * width + offset.X + left;
                part.Pbgra.AsSpan(y * part.Stride, part.Stride).CopyTo(canvas.Pbgra.AsSpan(row * 4));
            }
            frames.Add((canvas, delayMs));
        }
        return frames;
    }
}
