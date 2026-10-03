using DeskPet.App.Characters;
using DeskPet.App.Windowing;
using Microsoft.Win32;

namespace DeskPet.App.Tests;

public sealed class CharacterCatalogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskpet-characters-" + Guid.NewGuid().ToString("N"));

    private string BuiltIn => Path.Combine(_dir, "builtin");

    private string User => Path.Combine(_dir, "user");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static void AddCharacter(string root, string id, string? infoJson = null)
    {
        var directory = Path.Combine(root, id);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "manifest.json"), "{}");
        if (infoJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, CharacterCatalog.InfoFileName), infoJson);
        }
    }

    [Fact]
    public void Lists_built_in_then_user_characters_with_display_names()
    {
        AddCharacter(BuiltIn, "pixel-girl", """{ "name": "橙发女孩" }""");
        AddCharacter(User, "cat", """{ "name": "猫" }""");

        var characters = CharacterCatalog.Scan(BuiltIn, User, new RecordingLogger<CharacterCatalog>());

        Assert.Equal(
            [new CharacterInfo("pixel-girl", "橙发女孩", Path.Combine(BuiltIn, "pixel-girl"), true),
             new CharacterInfo("cat", "猫", Path.Combine(User, "cat"), false)],
            characters);
    }

    [Fact]
    public void User_character_overrides_built_in_with_the_same_id()
    {
        AddCharacter(BuiltIn, "pixel-girl");
        AddCharacter(User, "pixel-girl");

        var character = Assert.Single(CharacterCatalog.Scan(BuiltIn, User, new RecordingLogger<CharacterCatalog>()));
        Assert.False(character.IsBuiltIn);
        Assert.Equal(Path.Combine(User, "pixel-girl"), character.Directory);
    }

    [Fact]
    public void Name_falls_back_to_the_id_and_bad_info_is_logged()
    {
        AddCharacter(User, "plain");
        AddCharacter(User, "broken", "{ not json");
        var logger = new RecordingLogger<CharacterCatalog>();

        var characters = CharacterCatalog.Scan(BuiltIn, User, logger);

        Assert.Equal(["broken", "plain"], characters.Select(c => c.Name));
        Assert.Single(logger.Entries);
    }

    [Fact]
    public void Folders_without_a_manifest_and_missing_roots_are_skipped()
    {
        Directory.CreateDirectory(Path.Combine(User, "empty"));

        Assert.Empty(CharacterCatalog.Scan(BuiltIn, User, new RecordingLogger<CharacterCatalog>()));
    }

    [Fact]
    public void Find_ignores_case()
    {
        AddCharacter(User, "Cat");
        using var catalog = new CharacterCatalog(BuiltIn, User, new RecordingLogger<CharacterCatalog>());

        Assert.Equal("Cat", catalog.Find("cat")?.Id);
        Assert.Null(catalog.Find("dog"));
    }

    [Fact]
    public void Refresh_picks_up_new_characters()
    {
        using var catalog = new CharacterCatalog(BuiltIn, User, new RecordingLogger<CharacterCatalog>());
        Assert.Empty(catalog.Characters);

        AddCharacter(User, "cat");
        catalog.Refresh();

        Assert.Equal("cat", Assert.Single(catalog.Characters).Id);
    }
}

public sealed class AutoStartTests : IDisposable
{
    // A throwaway key, so the test never touches the real Run key.
    private readonly string _keyPath = @"Software\ClaudePetTests-" + Guid.NewGuid().ToString("N");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    [Fact]
    public void Set_enables_and_disables_the_run_value()
    {
        var autoStart = new AutoStart(_keyPath);
        Assert.False(autoStart.IsEnabled());

        autoStart.Set(true, @"C:\Apps\DeskPet.App.exe");
        Assert.True(autoStart.IsEnabled());
        using (var key = Registry.CurrentUser.OpenSubKey(_keyPath))
        {
            Assert.Equal("\"C:\\Apps\\DeskPet.App.exe\"", key?.GetValue(AutoStart.DefaultValueName));
        }

        autoStart.Set(false, @"C:\Apps\DeskPet.App.exe");
        Assert.False(autoStart.IsEnabled());
    }
}
