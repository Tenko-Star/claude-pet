using System.IO;
using System.Text.Json;
using DeskPet.App.Rendering;
using Microsoft.Extensions.Logging;

namespace DeskPet.App.Characters;

/// <summary>A character package: a folder with <c>manifest.json</c>, <c>sprites/</c> and an optional <c>character.json</c>.</summary>
/// <param name="Id">The folder name.</param>
/// <param name="Name">Display name from <c>character.json</c>, or the id.</param>
public sealed record CharacterInfo(string Id, string Name, string Directory, bool IsBuiltIn);

/// <summary>Ids of character folders that changed on disk.</summary>
public sealed class CharacterCatalogChangedEventArgs(IReadOnlySet<string> ids) : EventArgs
{
    public IReadOnlySet<string> Ids { get; } = ids;
}

/// <summary>
/// Finds character packages in the built-in root (next to the executable) and the per-user root.
/// A user character overrides a built-in one with the same id. Watches both roots and raises
/// <see cref="Changed"/> on a thread-pool thread, debounced, when packages are added, edited or removed.
/// </summary>
public sealed class CharacterCatalog : IDisposable
{
    public const string InfoFileName = "character.json";
    public const string DefaultCharacterId = "pixel-girl";

    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly ILogger<CharacterCatalog> _logger;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly HashSet<string> _pendingIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _pendingLock = new();
    private readonly System.Threading.Timer _debounceTimer;

    public CharacterCatalog(string builtInRoot, string userRoot, ILogger<CharacterCatalog> logger)
    {
        BuiltInRoot = builtInRoot;
        UserRoot = userRoot;
        _logger = logger;
        _debounceTimer = new System.Threading.Timer(_ => RaiseChanged());
        Characters = Scan(builtInRoot, userRoot, logger);
    }

    public static string DefaultBuiltInRoot => Path.Combine(AppContext.BaseDirectory, "characters");

    public static string DefaultUserRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudePet", "characters");

    public string BuiltInRoot { get; }

    public string UserRoot { get; }

    /// <summary>Built-in characters first, then user characters, each sorted by name.</summary>
    public IReadOnlyList<CharacterInfo> Characters { get; private set; }

    public event EventHandler<CharacterCatalogChangedEventArgs>? Changed;

    public CharacterInfo? Find(string id) =>
        Characters.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Rescans both roots. Call from the thread that reads <see cref="Characters"/>.</summary>
    public void Refresh() => Characters = Scan(BuiltInRoot, UserRoot, _logger);

    /// <summary>Creates the user root if needed and starts watching both roots.</summary>
    public void StartWatching()
    {
        try
        {
            Directory.CreateDirectory(UserRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not create the user character folder {Path}.", UserRoot);
        }

        foreach (var root in new[] { BuiltInRoot, UserRoot })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Created += (_, e) => OnFileEvent(e.Name);
            watcher.Changed += (_, e) => OnFileEvent(e.Name);
            watcher.Deleted += (_, e) => OnFileEvent(e.Name);
            watcher.Renamed += (_, e) =>
            {
                OnFileEvent(e.OldName);
                OnFileEvent(e.Name);
            };
            watcher.Error += (_, e) => _logger.LogWarning(e.GetException(), "Character folder watcher failed for {Path}.", root);
            watcher.EnableRaisingEvents = true;
            _watchers.Add(watcher);
        }
    }

    public void Dispose()
    {
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _debounceTimer.Dispose();
    }

    /// <summary>Lists the valid character folders under both roots; user entries replace built-in ones by id.</summary>
    public static IReadOnlyList<CharacterInfo> Scan(string builtInRoot, string userRoot, ILogger logger)
    {
        var found = new Dictionary<string, CharacterInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, builtIn) in new[] { (builtInRoot, true), (userRoot, false) })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    if (!File.Exists(Path.Combine(directory, SpriteLibrary.ManifestFileName)))
                    {
                        continue;
                    }
                    var id = Path.GetFileName(directory);
                    found[id] = new CharacterInfo(id, ReadName(directory, logger) ?? id, directory, builtIn);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not list characters in {Path}.", root);
            }
        }

        return found.Values
            .OrderBy(c => c.IsBuiltIn ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static string? ReadName(string directory, ILogger logger)
    {
        var path = Path.Combine(directory, InfoFileName);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString())
                    ? name.GetString()
                    : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning(ex, "Ignoring unreadable {Path}.", path);
            return null;
        }
    }

    // Called on watcher threads. The first path segment is the character id.
    private void OnFileEvent(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
        {
            return;
        }
        var id = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        lock (_pendingLock)
        {
            _pendingIds.Add(id);
            _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseChanged()
    {
        HashSet<string> ids;
        lock (_pendingLock)
        {
            ids = new HashSet<string>(_pendingIds, StringComparer.OrdinalIgnoreCase);
            _pendingIds.Clear();
        }
        if (ids.Count > 0)
        {
            Changed?.Invoke(this, new CharacterCatalogChangedEventArgs(ids));
        }
    }
}
