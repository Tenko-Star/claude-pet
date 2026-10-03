using DeskPet.App.Animation;

namespace DeskPet.App.Tests;

public class ManifestParserTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    // Smallest valid manifest; tests replace parts of it to make it invalid.
    private const string Minimal = """
        {
          "canvas": [2, 2],
          "stage": { "size": [2, 3], "characterOffset": [0, 1] },
          "layers": ["hair", "body", "eyes"],
          "hair": { "frames": ["h.png"], "frameMs": 100 },
          "eyes": { "blink": [["e.png", 10]], "intervalMs": [50, 60] },
          "mouth": { "open": "m.png", "talkToggleMs": 20 },
          "states": { "idle": { "body": "b.png", "eyes": "blink", "mouth": true } }
        }
        """;

    [Fact]
    public void Parses_real_manifest_geometry_and_shared_layers()
    {
        var manifest = TestAssets.LoadManifest();

        Assert.Equal(119, manifest.Width);
        Assert.Equal(129, manifest.Height);
        Assert.Equal(119, manifest.StageWidth);
        Assert.Equal(153, manifest.StageHeight);
        Assert.Equal(new PixelPoint(0, 24), manifest.CharacterOffset);
        Assert.Equal(["hair", "body", "eyes", "mouth", "fx"], manifest.Layers);

        Assert.Equal(["hair_c.png", "hair_l.png", "hair_c.png", "hair_r.png"], manifest.Hair.Frames);
        Assert.Equal(Ms(800), manifest.Hair.FrameDuration);
        Assert.Equal(
            [new BlinkStep("eye_half.png", Ms(60)), new BlinkStep("eye_closed.png", Ms(90)), new BlinkStep("eye_half.png", Ms(60))],
            manifest.Blink.Steps);
        Assert.Equal(Ms(2500), manifest.Blink.MinInterval);
        Assert.Equal(Ms(5500), manifest.Blink.MaxInterval);
        Assert.Equal(new MouthSpec("mouth_open.png", Ms(140)), manifest.Mouth);
    }

    [Fact]
    public void Parses_real_manifest_states()
    {
        var states = TestAssets.LoadManifest().States;

        Assert.Equal(["idle", "think", "working", "notice", "done", "error", "sleep"], states.Keys);

        var idle = states["idle"];
        Assert.Equal("main.png", idle.Body);
        Assert.Equal(EyesMode.Blink, idle.Eyes);
        Assert.True(idle.Mouth);
        Assert.Null(idle.Fx);

        var think = states["think"];
        Assert.Equal([new BodyFrame("main_think_mid.png", Ms(90))], think.Enter);
        Assert.Equal([new BodyFrame("main_think_mid.png", Ms(90))], think.Exit);
        Assert.False(think.Mouth);
        var thinkFx = Assert.IsType<LoopFx>(think.Fx);
        Assert.Equal(new PixelPoint(78, 23), thinkFx.Anchor);
        Assert.Equal(4, thinkFx.Frames.Count);
        Assert.Empty(thinkFx.Frames[3].Sprites);

        var tap = states["working"].Tap!;
        Assert.Equal("main_work_tap.png", tap.Frame);
        Assert.Equal(Ms(90), tap.Down);
        Assert.Equal(new TimeRange(Ms(90), Ms(210)), tap.Gap);
        Assert.Equal((3, 6), (tap.BurstMin, tap.BurstMax));
        Assert.Equal(new TimeRange(Ms(400), Ms(1100)), tap.Pause);
        Assert.Equal(new TapPace(3, 6, new TimeRange(Ms(400), Ms(1100))), tap.Paces["active"]);
        Assert.Equal(new TapPace(1, 3, new TimeRange(Ms(1200), Ms(2500))), tap.Paces["composing"]);

        var pop = Assert.IsType<PopFx>(states["notice"].Fx);
        Assert.Equal(4, pop.Pop.Count);
        Assert.Equal(new FxHold("fx_bang.png", -1, Ms(400)), pop.Hold);
        Assert.Equal(Ms(3500), pop.RepeatInterval);

        var done = states["done"];
        Assert.Equal(EyesMode.Off, done.Eyes);
        Assert.Equal(Ms(2200), done.Duration);
        Assert.Equal("idle", done.Then);

        Assert.Equal(EyesMode.Off, states["error"].Eyes);

        var sleep = states["sleep"];
        Assert.Equal(EyesMode.Fixed, sleep.Eyes);
        Assert.Equal("eye_sleep.png", sleep.EyesFile);
        Assert.Equal(Ms(1600), sleep.HairFrameDuration);
    }

    [Fact]
    public void Parses_real_manifest_reactions()
    {
        var reaction = TestAssets.LoadManifest().Reactions["toolFailure"];

        Assert.Equal(new PixelPoint(30, 52), reaction.Anchor);
        Assert.Equal([Ms(120), Ms(120), Ms(1000)], reaction.Frames.Select(f => f.Duration));
        Assert.Equal(new FxSprite("fx_sweat.png", 0, -3), reaction.Frames[0].Sprites.Single());
    }

    [Fact]
    public void All_referenced_sprites_exist()
    {
        var manifest = TestAssets.LoadManifest();
        foreach (var file in manifest.AllFiles)
        {
            Assert.True(File.Exists(Path.Combine(TestAssets.RuntimeDirectory, "sprites", file)), file);
        }
    }

    [Fact]
    public void Character_and_effect_files_are_separated()
    {
        var manifest = TestAssets.LoadManifest();

        Assert.Contains("main_work_tap.png", manifest.CharacterFiles);
        Assert.Contains("eye_sleep.png", manifest.CharacterFiles);
        Assert.DoesNotContain("fx_dot.png", manifest.CharacterFiles);
        Assert.Contains("fx_dot.png", manifest.EffectFiles);
        Assert.Contains("fx_sweat.png", manifest.EffectFiles);
    }

    [Fact]
    public void Minimal_manifest_parses_with_defaults()
    {
        var manifest = ManifestParser.Parse(Minimal);

        var idle = Assert.Single(manifest.States).Value;
        Assert.Empty(idle.Enter);
        Assert.Empty(idle.Exit);
        Assert.Null(idle.Tap);
        Assert.Null(idle.Duration);
        Assert.Empty(manifest.Reactions);
    }

    [Theory]
    [InlineData("\"layers\": [\"hair\", \"body\", \"eyes\"]", "\"layers\": [\"hair\", \"cape\"]", "unknown layer")]
    [InlineData("\"layers\": [\"hair\", \"body\", \"eyes\"]", "\"layers\": [\"hair\", \"hair\"]", "listed twice")]
    [InlineData("\"layers\": [\"hair\", \"body\", \"eyes\"]", "\"layers\": []", "non-empty")]
    [InlineData("\"characterOffset\": [0, 1]", "\"characterOffset\": [0, 2]", "does not fit")]
    [InlineData("\"canvas\": [2, 2]", "\"canvas\": [2]", "canvas")]
    [InlineData("\"frameMs\": 100", "\"frameMs\": 0", "positive")]
    [InlineData("\"intervalMs\": [50, 60]", "\"intervalMs\": [60, 50]", "below min")]
    [InlineData("\"body\": \"b.png\", ", "", "missing 'body'")]
    [InlineData("\"mouth\": true", "\"mouth\": 1", "true or false")]
    [InlineData("\"mouth\": true", "\"mouth\": true, \"durationMs\": 10", "needs 'then'")]
    [InlineData("\"mouth\": true", "\"mouth\": true, \"durationMs\": 10, \"then\": \"nowhere\"", "unknown state 'nowhere'")]
    [InlineData("\"mouth\": true", "\"mouth\": true, \"fx\": { \"anchor\": [0, 0] }", "'loop' or 'pop'")]
    [InlineData("\"mouth\": true", "\"mouth\": true, \"fx\": { \"anchor\": [0, 0], \"loop\": [{ \"ms\": 5, \"sprites\": [[\"x.png\", 1]] }] }", "[file, dx, dy]")]
    [InlineData("\"mouth\": true", "\"mouth\": true, \"tap\": { \"frame\": \"t.png\", \"downMs\": 5, \"gapMs\": [1, 2], \"burst\": [0, 2], \"pauseMs\": [1, 2] }", "burst")]
    [InlineData("\"mouth\": true", "\"mouth\": true, \"paces\": { \"active\": { \"taps\": [1, 2], \"pauseMs\": [1, 2] } }", "'paces' needs 'tap'")]
    [InlineData("\"mouth\": true", "\"mouth\": true, \"tap\": { \"frame\": \"t.png\", \"downMs\": 5, \"gapMs\": [1, 2], \"burst\": [1, 2], \"pauseMs\": [1, 2] }, \"paces\": { \"active\": { \"taps\": [3, 2], \"pauseMs\": [1, 2] } }", "pace 'active'")]
    [InlineData("\"states\": { \"idle\": { \"body\": \"b.png\", \"eyes\": \"blink\", \"mouth\": true } }", "\"states\": {}", "must not be empty")]
    public void Rejects_malformed_manifest(string find, string replace, string expectedMessagePart)
    {
        Assert.Contains(find, Minimal);
        var ex = Assert.Throws<InvalidDataException>(() => ManifestParser.Parse(Minimal.Replace(find, replace)));
        Assert.Contains(expectedMessagePart, ex.Message);
    }
}
