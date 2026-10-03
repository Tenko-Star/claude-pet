using DeskPet.App.Animation;
using DeskPet.App.Characters;
using DeskPet.App.Rendering;
using Microsoft.Extensions.Logging;

namespace DeskPet.App.Tests;

internal static class TestAssets
{
    /// <summary>The built-in character package, copied next to the test assembly by the DeskPet.App build.</summary>
    public static string RuntimeDirectory => Path.Combine(AppContext.BaseDirectory, "characters", CharacterCatalog.DefaultCharacterId);

    public static SpriteManifest LoadManifest() =>
        ManifestParser.Parse(File.ReadAllText(Path.Combine(RuntimeDirectory, SpriteLibrary.ManifestFileName)));
}

/// <summary>Records log calls so tests can assert on them.</summary>
internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
