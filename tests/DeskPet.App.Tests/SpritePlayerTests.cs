using DeskPet.App.Animation;

namespace DeskPet.App.Tests;

public class SpritePlayerTests
{
    private const int OffsetY = 24;

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    // Random picks are pinned: intervals use the minimum (first blink at 2500 ms, tap pause 400 ms,
    // tap gap 90 ms) and bursts use the minimum count (3 presses).
    private static SpritePlayer CreatePlayer(out RecordingLogger<SpritePlayer> logger, int? blinkMs = null)
    {
        logger = new RecordingLogger<SpritePlayer>();
        return new SpritePlayer(
            TestAssets.LoadManifest(),
            logger,
            (min, max) => blinkMs is { } fixedMs && min == Ms(2500) && max == Ms(5500) ? Ms(fixedMs) : min,
            pickCount: (min, _) => min);
    }

    private static SpritePlayer CreatePlayer() => CreatePlayer(out _, blinkMs: 1_000_000);

    private static string[] Files(FrameState frame) => frame.Sprites.Select(s => s.File).ToArray();

    private static string Body(FrameState frame) => frame.Sprites[1].File;

    [Theory]
    [InlineData(0, "hair_c.png")]
    [InlineData(799, "hair_c.png")]
    [InlineData(800, "hair_l.png")]
    [InlineData(1600, "hair_c.png")]
    [InlineData(2400, "hair_r.png")]
    [InlineData(3199, "hair_r.png")]
    [InlineData(3200, "hair_c.png")]
    public void Idle_hair_loops_through_manifest_frames(int now, string expectedHair)
    {
        var frame = CreatePlayer().Evaluate(Ms(now));
        Assert.Equal(
            [new SpritePlacement(expectedHair, 0, OffsetY), new SpritePlacement("main.png", 0, OffsetY)],
            frame.Sprites);
    }

    [Fact]
    public void NextChangeAt_is_the_nearest_layer_boundary()
    {
        var player = CreatePlayer(out _, blinkMs: 1200);

        Assert.Equal(Ms(800), player.Evaluate(Ms(0)).NextChangeAt);
        Assert.Equal(Ms(1200), player.Evaluate(Ms(800)).NextChangeAt);
        Assert.Equal(Ms(1260), player.Evaluate(Ms(1200)).NextChangeAt);
        Assert.Equal(Ms(1600), player.Evaluate(Ms(1410)).NextChangeAt);
    }

    [Fact]
    public void Blink_patch_is_drawn_above_the_body_in_manifest_order()
    {
        var player = CreatePlayer(out _, blinkMs: 1200);
        Assert.Equal(["hair_l.png", "main.png", "eye_closed.png"], Files(player.Evaluate(Ms(1300))));
    }

    [Fact]
    public void Talking_toggles_mouth_only_in_states_that_allow_it()
    {
        var player = CreatePlayer();
        Assert.DoesNotContain("mouth_open.png", Files(player.Evaluate(Ms(100))));

        player.SetTalking(true, Ms(100));
        Assert.True(player.IsTalking);
        Assert.Contains("mouth_open.png", Files(player.Evaluate(Ms(100))));
        Assert.Contains("mouth_open.png", Files(player.Evaluate(Ms(239))));
        Assert.DoesNotContain("mouth_open.png", Files(player.Evaluate(Ms(240))));
        Assert.Equal(Ms(380), player.Evaluate(Ms(300)).NextChangeAt);
        Assert.Contains("mouth_open.png", Files(player.Evaluate(Ms(380))));

        player.SetState("think", Ms(400));
        Assert.DoesNotContain("mouth_open.png", Files(player.Evaluate(Ms(520))));

        player.SetTalking(false, Ms(600));
        player.SetState("idle", Ms(600));
        Assert.DoesNotContain("mouth_open.png", Files(player.Evaluate(Ms(800))));
    }

    [Fact]
    public void Enter_frames_play_before_the_body_and_effects_start_after_them()
    {
        var player = CreatePlayer();
        player.SetState("think", Ms(1000));

        var entering = player.Evaluate(Ms(1000));
        Assert.Equal("main_think_mid.png", Body(entering));
        Assert.DoesNotContain("fx_dot.png", Files(entering));
        Assert.Equal(Ms(1090), entering.NextChangeAt);

        var settled = player.Evaluate(Ms(1090));
        Assert.Equal("main_think.png", Body(settled));
        // fx_dot.png is 4x4: bottom edge at anchor y 23, left edge at anchor x 78.
        Assert.Equal(new SpritePlacement("fx_dot.png", 78, 23, AlignBottom: true), settled.Sprites[^1]);
        Assert.Equal(Ms(1410), settled.NextChangeAt);

        Assert.Equal(3, Files(player.Evaluate(Ms(1730))).Count(f => f == "fx_dot.png"));
        Assert.DoesNotContain("fx_dot.png", Files(player.Evaluate(Ms(2210))));
        Assert.Single(Files(player.Evaluate(Ms(2570))), f => f == "fx_dot.png");
    }

    [Fact]
    public void Exit_frames_of_the_old_state_play_before_the_new_state()
    {
        var player = CreatePlayer();
        player.SetState("think", Ms(0));
        player.SetState("notice", Ms(1000));

        Assert.Equal("main_think_mid.png", Body(player.Evaluate(Ms(1000))));
        Assert.Equal("main_notice_mid.png", Body(player.Evaluate(Ms(1090))));
        Assert.Equal("main_notice.png", Body(player.Evaluate(Ms(1180))));
        Assert.Equal("notice", player.CurrentState);
    }

    [Fact]
    public void Working_taps_in_bursts_then_pauses()
    {
        var player = CreatePlayer();
        player.SetState("working", Ms(0));

        // Pause 400 ms, then 3 presses of 90 ms separated by 90 ms gaps, then another 400 ms pause.
        var expected = new (int At, string Body)[]
        {
            (0, "main_work.png"), (400, "main_work_tap.png"), (490, "main_work.png"),
            (580, "main_work_tap.png"), (670, "main_work.png"), (760, "main_work_tap.png"),
            (850, "main_work.png"), (1249, "main_work.png"), (1250, "main_work_tap.png"),
        };
        foreach (var (at, body) in expected)
        {
            Assert.Equal(body, Body(player.Evaluate(Ms(at))));
        }
    }

    [Fact]
    public void Composing_pace_taps_less_often()
    {
        var player = CreatePlayer();
        player.SetPace("composing");
        player.SetState("working", Ms(0));

        // Composing: pause 1200 ms, bursts of a single 90 ms press.
        var expected = new (int At, string Body)[]
        {
            (0, "main_work.png"), (1199, "main_work.png"), (1200, "main_work_tap.png"),
            (1290, "main_work.png"), (2489, "main_work.png"), (2490, "main_work_tap.png"),
        };
        foreach (var (at, body) in expected)
        {
            Assert.Equal(body, Body(player.Evaluate(Ms(at))));
        }
    }

    [Fact]
    public void Changing_pace_keeps_the_state_and_applies_from_the_next_pause()
    {
        var player = CreatePlayer();
        player.SetPace("active");
        player.SetState("working", Ms(0));
        Assert.Equal("main_work_tap.png", Body(player.Evaluate(Ms(400))));

        player.SetPace("composing");

        // The running burst of 3 presses finishes; the following pause is the composing 1200 ms.
        Assert.Equal("working", player.CurrentState);
        var expected = new (int At, string Body)[]
        {
            (500, "main_work.png"), (580, "main_work_tap.png"), (760, "main_work_tap.png"),
            (850, "main_work.png"), (2049, "main_work.png"), (2050, "main_work_tap.png"),
        };
        foreach (var (at, body) in expected)
        {
            Assert.Equal(body, Body(player.Evaluate(Ms(at))));
        }
    }

    [Fact]
    public void Notice_pops_then_holds_with_a_bob_and_pops_again()
    {
        var player = CreatePlayer();
        player.SetState("notice", Ms(0));

        // Enter frame 90 ms; pop frames 60 + 80 + 140 + 160 = 440 ms; then hold; repeat 3500 ms after the pop.
        Assert.Equal(new SpritePlacement("fx_bang_squash.png", 78, 28, AlignBottom: true), player.Evaluate(Ms(90)).Sprites[^1]);
        Assert.Equal(new SpritePlacement("fx_bang_stretch.png", 78, 25, AlignBottom: true), player.Evaluate(Ms(150)).Sprites[^1]);
        Assert.Equal(new SpritePlacement("fx_bang.png", 78, 28, AlignBottom: true), player.Evaluate(Ms(530)).Sprites[^1]);
        Assert.Equal(new SpritePlacement("fx_bang.png", 78, 27, AlignBottom: true), player.Evaluate(Ms(930)).Sprites[^1]);
        Assert.Equal(new SpritePlacement("fx_bang.png", 78, 28, AlignBottom: true), player.Evaluate(Ms(1330)).Sprites[^1]);
        Assert.Equal("fx_bang_squash.png", player.Evaluate(Ms(4030)).Sprites[^1].File);
    }

    [Fact]
    public void Done_hides_the_eye_layer_and_returns_to_its_then_state()
    {
        var player = CreatePlayer(out _, blinkMs: 100);
        player.SetState("done", Ms(0));

        var frame = player.Evaluate(Ms(150));
        Assert.Equal("main_done.png", Body(frame));
        Assert.DoesNotContain("eye_closed.png", Files(frame));
        Assert.Contains("fx_star_big.png", Files(frame));
        Assert.True(player.Evaluate(Ms(14_999)).NextChangeAt <= Ms(15_000));

        Assert.Equal("done", player.CurrentState);
        Assert.Equal("main.png", Body(player.Evaluate(Ms(15_000))));
        Assert.Equal("idle", player.CurrentState);
    }

    [Fact]
    public void CycleComplete_waits_for_enter_frames_pops_and_loop_wraps()
    {
        var player = CreatePlayer();

        // notice: 90 ms enter frame, then a 440 ms pop before the hold.
        player.SetState("notice", Ms(0));
        Assert.False(player.CycleComplete(Ms(0), Ms(50), out _));
        Assert.False(player.CycleComplete(Ms(0), Ms(529), out _));
        Assert.True(player.CycleComplete(Ms(0), Ms(530), out _));

        // done: notice's 90 ms exit frame, then a 440 ms star loop from 1090 ms.
        player.SetState("done", Ms(1000));
        Assert.False(player.CycleComplete(Ms(1500), Ms(1500), out var wakeAt));
        Assert.Equal(Ms(1530), wakeAt);
        Assert.True(player.CycleComplete(Ms(1500), Ms(1530), out _));
    }

    [Fact]
    public void RestartDuration_counts_the_duration_again_from_now()
    {
        var player = CreatePlayer();
        player.SetState("done", Ms(0));

        player.RestartDuration(Ms(10_000));

        player.Evaluate(Ms(24_999));
        Assert.Equal("done", player.CurrentState);
        player.Evaluate(Ms(25_000));
        Assert.Equal("idle", player.CurrentState);
    }

    [Fact]
    public void Sleep_shows_fixed_eyes_and_slows_the_hair()
    {
        var player = CreatePlayer();
        player.SetState("sleep", Ms(0));

        Assert.Equal(["hair_c.png", "main.png", "eye_sleep.png"], Files(player.Evaluate(Ms(0))).Take(3));
        // The first hair frame was scheduled in idle (800 ms); later frames use the 1600 ms sleep duration.
        Assert.Equal("hair_l.png", Files(player.Evaluate(Ms(800)))[0]);
        Assert.Equal("hair_l.png", Files(player.Evaluate(Ms(2399)))[0]);
        Assert.Equal("hair_c.png", Files(player.Evaluate(Ms(2400)))[0]);
    }

    [Fact]
    public void Reaction_overlays_the_current_state_once()
    {
        var player = CreatePlayer();
        player.SetState("working", Ms(0));
        player.TriggerReaction("toolFailure", Ms(100));

        var frame = player.Evaluate(Ms(100));
        Assert.Equal(new SpritePlacement("fx_sweat.png", 30, 49, AlignBottom: true), frame.Sprites[^1]);
        Assert.Equal("working", player.CurrentState);
        Assert.Equal(new SpritePlacement("fx_sweat.png", 30, 52, AlignBottom: true), player.Evaluate(Ms(340)).Sprites[^1]);
        Assert.DoesNotContain("fx_sweat.png", Files(player.Evaluate(Ms(1340))));
    }

    [Fact]
    public void Unknown_state_and_reaction_are_logged_and_ignored()
    {
        var player = CreatePlayer(out var logger);
        player.SetState("dancing", Ms(0));
        player.TriggerReaction("sneeze", Ms(0));

        Assert.Equal("idle", player.CurrentState);
        Assert.Equal(2, logger.Entries.Count);
        Assert.Contains("'dancing'", logger.Entries[0].Message);
        Assert.Contains("'sneeze'", logger.Entries[1].Message);
    }

    [Fact]
    public void Setting_the_current_state_again_does_not_restart_it()
    {
        var player = CreatePlayer();
        player.SetState("think", Ms(0));
        player.SetState("think", Ms(50));

        Assert.Equal("main_think.png", Body(player.Evaluate(Ms(90))));
    }
}
