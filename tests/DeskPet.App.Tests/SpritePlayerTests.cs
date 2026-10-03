using DeskPet.App.Animation;
using Microsoft.Extensions.Logging;

namespace DeskPet.App.Tests;

public class SpritePlayerTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static SpritePlayer CreatePlayer(out RecordingLogger<SpritePlayer> logger, int firstBlinkMs = 1_000_000)
    {
        logger = new RecordingLogger<SpritePlayer>();
        return new SpritePlayer(TestAssets.LoadManifest(), logger, (_, _) => Ms(firstBlinkMs));
    }

    [Theory]
    [InlineData(0, "hair_c.png")]
    [InlineData(799, "hair_c.png")]
    [InlineData(800, "hair_l.png")]
    [InlineData(1600, "hair_c.png")]
    [InlineData(2400, "hair_r.png")]
    [InlineData(3199, "hair_r.png")]
    [InlineData(3200, "hair_c.png")]
    public void Hair_loops_through_manifest_frames(int now, string expectedHair)
    {
        var player = CreatePlayer(out _);
        var frame = player.Evaluate(Ms(now));
        Assert.Equal([expectedHair, "main.png"], frame.Files);
    }

    [Fact]
    public void NextChangeAt_is_the_nearest_layer_boundary()
    {
        var player = CreatePlayer(out _, firstBlinkMs: 1200);

        Assert.Equal(Ms(800), player.Evaluate(Ms(0)).NextChangeAt);
        Assert.Equal(Ms(1200), player.Evaluate(Ms(800)).NextChangeAt);
        Assert.Equal(Ms(1260), player.Evaluate(Ms(1200)).NextChangeAt);
        Assert.Equal(Ms(1600), player.Evaluate(Ms(1410)).NextChangeAt);
    }

    [Fact]
    public void Blink_patch_is_drawn_above_main_in_manifest_order()
    {
        var player = CreatePlayer(out _, firstBlinkMs: 1200);
        Assert.Equal(["hair_l.png", "main.png", "eye_closed.png"], player.Evaluate(Ms(1300)).Files);
    }

    [Fact]
    public void Talking_toggles_mouth_from_when_it_was_switched_on()
    {
        var player = CreatePlayer(out _);
        Assert.DoesNotContain("mouth_open.png", player.Evaluate(Ms(100)).Files);

        player.SetTalking(true, Ms(100));
        Assert.True(player.IsTalking);
        Assert.Contains("mouth_open.png", player.Evaluate(Ms(100)).Files);
        Assert.Contains("mouth_open.png", player.Evaluate(Ms(239)).Files);
        Assert.DoesNotContain("mouth_open.png", player.Evaluate(Ms(240)).Files);
        Assert.Contains("mouth_open.png", player.Evaluate(Ms(380)).Files);
        Assert.Equal(Ms(240), player.Evaluate(Ms(200)).NextChangeAt);
        Assert.Equal("mouth_open.png", player.Evaluate(Ms(100)).Files[^1]);

        player.SetTalking(false, Ms(400));
        Assert.DoesNotContain("mouth_open.png", player.Evaluate(Ms(400)).Files);
    }

    [Fact]
    public void Idle_status_is_accepted_without_logging()
    {
        var player = CreatePlayer(out var logger);
        player.SetStatus("idle");
        Assert.Equal("idle", player.CurrentStatus);
        Assert.Empty(logger.Entries);
    }

    [Theory]
    [InlineData("thinking")]
    [InlineData("IDLE")]
    [InlineData("")]
    public void Unknown_status_falls_back_to_idle_and_logs_a_warning(string status)
    {
        var player = CreatePlayer(out var logger);
        player.SetStatus(status);

        Assert.Equal("idle", player.CurrentStatus);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains($"'{status}'", entry.Message);
        Assert.Equal(["hair_c.png", "main.png"], player.Evaluate(Ms(0)).Files);
    }

    [Fact]
    public void Static_only_manifest_never_needs_a_timer()
    {
        var manifest = ManifestParser.Parse("""{"canvas":[1,1],"layers":["a"],"a":"a.png"}""");
        var player = new SpritePlayer(manifest, new RecordingLogger<SpritePlayer>());
        Assert.Equal(TimeSpan.MaxValue, player.Evaluate(Ms(5)).NextChangeAt);
    }
}
