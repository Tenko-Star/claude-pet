using System.IO;

namespace DeskPet.App.Animation;

/// <summary>Finds the pixel-girl <c>runtime</c> directory by looking for <c>runtime/manifest.json</c>.</summary>
public static class AssetLocator
{
    public const string ManifestFileName = "manifest.json";

    /// <summary>
    /// Returns the directory containing <c>manifest.json</c>. A configured root is tried first;
    /// otherwise each ancestor of <paramref name="startDirectory"/> is checked for
    /// <c>assets/runtime/manifest.json</c> or <c>runtime/manifest.json</c>.
    /// </summary>
    public static string FindRuntimeDirectory(string? configuredRoot, string startDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredRoot))
        {
            var root = Path.GetFullPath(configuredRoot);
            foreach (var candidate in new[] { root, Path.Combine(root, "runtime") })
            {
                if (File.Exists(Path.Combine(candidate, ManifestFileName)))
                {
                    return candidate;
                }
            }
            throw new DirectoryNotFoundException(
                $"Configured asset root '{root}' does not contain {ManifestFileName} or runtime/{ManifestFileName}.");
        }

        for (var dir = new DirectoryInfo(startDirectory); dir is not null; dir = dir.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(dir.FullName, "assets", "runtime"), Path.Combine(dir.FullName, "runtime") })
            {
                if (File.Exists(Path.Combine(candidate, ManifestFileName)))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find assets/runtime/{ManifestFileName} above '{startDirectory}'. Set DeskPet:AssetRoot.");
    }
}
