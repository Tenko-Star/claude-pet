using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace DeskPet.App.Windowing;

/// <summary>Window position (DIPs) and scale remembered between runs.</summary>
public sealed record WindowPlacement(double Left, double Top, int Scale);

/// <summary>Loads and saves <see cref="WindowPlacement"/> as a small JSON file.</summary>
public sealed class WindowStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly ILogger<WindowStateStore> _logger;

    public WindowStateStore(ILogger<WindowStateStore> logger, string? filePath = null)
    {
        _logger = logger;
        FilePath = filePath ?? DefaultPath;
    }

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudePet", "DeskPet", "window.json");

    public string FilePath { get; }

    /// <summary>Returns the saved placement, or null when there is none or it cannot be read.</summary>
    public WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }
            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(FilePath), JsonOptions);
            if (placement is null || !double.IsFinite(placement.Left) || !double.IsFinite(placement.Top))
            {
                return null;
            }
            return placement with { Scale = WindowGeometry.ClampScale(placement.Scale) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Ignoring unreadable window state file {Path}.", FilePath);
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(placement, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save window state to {Path}.", FilePath);
        }
    }
}
