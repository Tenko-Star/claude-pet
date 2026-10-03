using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskPet.App.Animation;

namespace DeskPet.App.Rendering;

/// <summary>Decoded sprite layers for one manifest, keyed by the file names the manifest uses.</summary>
public sealed class SpriteLibrary
{
    private readonly Dictionary<string, PixelBuffer> _sprites;

    private SpriteLibrary(string runtimeDirectory, SpriteManifest manifest, Dictionary<string, PixelBuffer> sprites)
    {
        RuntimeDirectory = runtimeDirectory;
        Manifest = manifest;
        _sprites = sprites;
    }

    public string RuntimeDirectory { get; }

    public SpriteManifest Manifest { get; }

    /// <summary>Reads the manifest in <paramref name="runtimeDirectory"/> and decodes every sprite it references.</summary>
    public static SpriteLibrary Load(string runtimeDirectory)
    {
        var manifest = ManifestParser.Parse(File.ReadAllText(Path.Combine(runtimeDirectory, AssetLocator.ManifestFileName)));
        var sprites = new Dictionary<string, PixelBuffer>(StringComparer.Ordinal);
        foreach (var file in manifest.AllFiles)
        {
            var buffer = Decode(ResolveFile(runtimeDirectory, file));
            if (buffer.Width != manifest.Width || buffer.Height != manifest.Height)
            {
                throw new InvalidDataException(
                    $"Sprite '{file}' is {buffer.Width}x{buffer.Height}, manifest canvas is {manifest.Width}x{manifest.Height}.");
            }
            sprites[file] = buffer;
        }
        return new SpriteLibrary(runtimeDirectory, manifest, sprites);
    }

    public PixelBuffer this[string file] => _sprites[file];

    /// <summary>Composites the given files bottom to top at 1x.</summary>
    public PixelBuffer Compose(IEnumerable<string> files) =>
        PixelCompositor.Compose(Manifest.Width, Manifest.Height, files.Select(f => _sprites[f]));

    /// <summary>Decodes any WPF-supported image into premultiplied BGRA without resampling.</summary>
    public static PixelBuffer Decode(string path)
    {
        using var stream = File.OpenRead(path);
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        return ToPixelBuffer(frame);
    }

    public static PixelBuffer ToPixelBuffer(BitmapSource source)
    {
        var converted = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var buffer = PixelBuffer.Empty(converted.PixelWidth, converted.PixelHeight);
        converted.CopyPixels(buffer.Pbgra, buffer.Stride, 0);
        return buffer;
    }

    // Manifest entries are bare file names; the package keeps them under runtime/sprites.
    private static string ResolveFile(string runtimeDirectory, string file)
    {
        foreach (var candidate in new[] { Path.Combine(runtimeDirectory, "sprites", file), Path.Combine(runtimeDirectory, file) })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        throw new FileNotFoundException($"Sprite '{file}' not found under '{runtimeDirectory}'.", file);
    }
}
