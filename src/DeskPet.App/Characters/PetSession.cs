using DeskPet.App.Animation;
using DeskPet.App.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DeskPet.App.Characters;

/// <summary>One loaded character: its decoded sprites and the controller animating them.</summary>
public sealed record PetSession(CharacterInfo Character, SpriteLibrary Sprites, PetController Pet);

/// <summary>Loads a character package into a <see cref="PetSession"/>.</summary>
public sealed class PetSessionFactory(ILogger<SpritePlayer> playerLogger, IOptions<DeskPetOptions> options)
{
    /// <summary>
    /// Loads and decodes the package. Throws <see cref="System.IO.IOException"/>,
    /// <see cref="System.IO.InvalidDataException"/> and the other errors <see cref="IsLoadError"/> accepts.
    /// </summary>
    /// <param name="now">Animation clock time the new player starts at.</param>
    public PetSession Create(CharacterInfo character, TimeSpan now)
    {
        var sprites = SpriteLibrary.Load(character.Directory);
        var player = new SpritePlayer(sprites.Manifest, playerLogger, start: now);
        return new PetSession(character, sprites, new PetController(player, options.Value.SleepAfter));
    }

    /// <summary>Errors a broken or half-copied character package can cause while loading.</summary>
    public static bool IsLoadError(Exception ex) =>
        ex is System.IO.IOException or System.IO.InvalidDataException or UnauthorizedAccessException
            or System.Text.Json.JsonException or NotSupportedException;
}
