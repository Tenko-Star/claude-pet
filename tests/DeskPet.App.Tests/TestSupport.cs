using DeskPet.App.Animation;
using Microsoft.Extensions.Logging;

namespace DeskPet.App.Tests;

internal static class TestAssets
{
    public static string RuntimeDirectory => AssetLocator.FindRuntimeDirectory(null, AppContext.BaseDirectory);

    public static SpriteManifest LoadManifest() =>
        ManifestParser.Parse(File.ReadAllText(Path.Combine(RuntimeDirectory, AssetLocator.ManifestFileName)));
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
