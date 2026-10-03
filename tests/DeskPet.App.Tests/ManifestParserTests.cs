using DeskPet.App.Animation;

namespace DeskPet.App.Tests;

public class ManifestParserTests
{
    [Fact]
    public void Parses_real_manifest_into_layer_kinds_in_order()
    {
        var manifest = TestAssets.LoadManifest();

        Assert.Equal(119, manifest.Width);
        Assert.Equal(129, manifest.Height);
        Assert.Collection(manifest.Layers,
            l =>
            {
                var loop = Assert.IsType<LoopLayer>(l);
                Assert.Equal("hair", loop.Name);
                Assert.Equal(["hair_c.png", "hair_l.png", "hair_c.png", "hair_r.png"], loop.Frames);
                Assert.Equal(TimeSpan.FromMilliseconds(800), loop.FrameDuration);
            },
            l => Assert.Equal(new StaticLayer("main", "main.png"), l),
            l =>
            {
                var blink = Assert.IsType<BlinkLayer>(l);
                Assert.Equal(
                    [new BlinkStep("eye_half.png", TimeSpan.FromMilliseconds(60)),
                     new BlinkStep("eye_closed.png", TimeSpan.FromMilliseconds(90)),
                     new BlinkStep("eye_half.png", TimeSpan.FromMilliseconds(60))],
                    blink.Steps);
                Assert.Equal(TimeSpan.FromMilliseconds(2500), blink.MinInterval);
                Assert.Equal(TimeSpan.FromMilliseconds(5500), blink.MaxInterval);
            },
            l => Assert.Equal(new TalkLayer("mouth", "mouth_open.png", TimeSpan.FromMilliseconds(140)), l));
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

    [Theory]
    [InlineData("""{"canvas":[1,1],"layers":["a"],"a":{"weird":1}}""", "unrecognized")]
    [InlineData("""{"canvas":[1,1],"layers":["a"]}""", "has no entry")]
    [InlineData("""{"canvas":[1,1],"layers":["a"],"a":{"frames":["x.png"]}}""", "frameMs")]
    [InlineData("""{"canvas":[1,1],"layers":["a"],"a":{"frames":["x.png"],"frameMs":0}}""", "positive")]
    [InlineData("""{"canvas":[1,1],"layers":["a"],"a":{"blink":[["x.png",10]],"intervalMs":[5,1]}}""", "below min")]
    [InlineData("""{"canvas":[1],"layers":["a"],"a":"x.png"}""", "canvas")]
    [InlineData("""{"canvas":[1,1],"layers":[]}""", "non-empty")]
    public void Rejects_malformed_manifest(string json, string expectedMessagePart)
    {
        var ex = Assert.Throws<InvalidDataException>(() => ManifestParser.Parse(json));
        Assert.Contains(expectedMessagePart, ex.Message);
    }

    [Fact]
    public void Layer_names_come_from_the_manifest()
    {
        var manifest = ManifestParser.Parse("""{"canvas":[2,3],"layers":["back","face"],"back":"b.png","face":{"open":"o.png","talkToggleMs":50}}""");

        Assert.Equal([new StaticLayer("back", "b.png"), new TalkLayer("face", "o.png", TimeSpan.FromMilliseconds(50))], manifest.Layers);
    }
}
