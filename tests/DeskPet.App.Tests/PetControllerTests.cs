using DeskPet.App.Animation;
using StatusHub.Contracts;

namespace DeskPet.App.Tests;

public class PetControllerTests
{
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(5);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    // Random picks are pinned to the minimum: tap pause 400 ms, gap 90 ms, bursts of 3 presses.
    private static PetController CreateController(SpriteManifest? manifest = null) =>
        new(new SpritePlayer(
            manifest ?? TestAssets.LoadManifest(),
            new RecordingLogger<SpritePlayer>(),
            (min, _) => min,
            pickCount: (min, _) => min), SleepAfter);

    private static StatusSnapshot Snapshot(ClaudeStatus status, WorkPace pace = WorkPace.Active) =>
        new(status, 1, DateTimeOffset.UnixEpoch, pace);

    private static StatusEvent Event(ClaudeStatus kind) => new(kind, "s1", DateTimeOffset.UnixEpoch);

    [Theory]
    [InlineData(ClaudeStatus.Idle, "idle")]
    [InlineData(ClaudeStatus.Thinking, "think")]
    [InlineData(ClaudeStatus.Working, "working")]
    [InlineData(ClaudeStatus.Waiting, "notice")]
    public void Snapshot_status_maps_to_a_manifest_state(ClaudeStatus status, string expected)
    {
        var pet = CreateController();
        pet.ApplySnapshot(Snapshot(status), Ms(0));

        Assert.Equal(expected, pet.Player.CurrentState);
    }

    [Fact]
    public void Done_is_locked_for_two_seconds_then_yields_to_activity_at_the_end_of_its_loop()
    {
        var pet = CreateController();
        pet.ApplyEvent(Event(ClaudeStatus.Done), Ms(0));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working), Ms(0));

        var frame = pet.Evaluate(Ms(1999));
        Assert.Equal("done", pet.Player.CurrentState);
        Assert.True(frame.NextChangeAt <= Ms(2000));

        // The lock ends at 2000 ms; done's 440 ms star loop wraps next at 2200 ms.
        frame = pet.Evaluate(Ms(2000));
        Assert.Equal("done", pet.Player.CurrentState);
        Assert.True(frame.NextChangeAt <= Ms(2200));

        pet.Evaluate(Ms(2200));
        Assert.Equal("working", pet.Player.CurrentState);
    }

    [Fact]
    public void Idle_does_not_cut_done_short()
    {
        var pet = CreateController();
        pet.ApplyEvent(Event(ClaudeStatus.Done), Ms(0));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Idle), Ms(0));

        pet.Evaluate(Ms(14_999));
        Assert.Equal("done", pet.Player.CurrentState);

        pet.Evaluate(Ms(15_000));
        Assert.Equal("idle", pet.Player.CurrentState);
    }

    [Fact]
    public void A_quick_turn_inside_the_lock_keeps_done_and_restarts_it()
    {
        var pet = CreateController();
        pet.ApplyEvent(Event(ClaudeStatus.Done), Ms(0));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Idle), Ms(0));

        pet.ApplySnapshot(Snapshot(ClaudeStatus.Thinking), Ms(500));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working), Ms(800));
        pet.ApplyEvent(Event(ClaudeStatus.ToolFailure), Ms(900));
        pet.ApplyEvent(Event(ClaudeStatus.Done), Ms(1200));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Idle), Ms(1200));

        foreach (var at in new[] { 1300, 2500, 16_199 })
        {
            var frame = pet.Evaluate(Ms(at));
            Assert.Equal("done", pet.Player.CurrentState);
            Assert.DoesNotContain(frame.Sprites, s => s.File == "fx_sweat.png");
        }

        pet.Evaluate(Ms(16_200));
        Assert.Equal("idle", pet.Player.CurrentState);
    }

    [Fact]
    public void Error_inside_the_done_lock_shows_when_the_lock_ends()
    {
        var pet = CreateController();
        pet.ApplyEvent(Event(ClaudeStatus.Done), Ms(0));
        pet.ApplyEvent(Event(ClaudeStatus.Error), Ms(500));

        // Locked until 2000 ms, then the star loop finishes at 2200 ms.
        pet.Evaluate(Ms(2199));
        Assert.Equal("done", pet.Player.CurrentState);

        pet.Evaluate(Ms(2200));
        Assert.Equal("error", pet.Player.CurrentState);
    }

    [Fact]
    public void States_switch_after_the_minimum_dwell_at_the_end_of_the_cycle_and_skip_to_the_latest()
    {
        var pet = CreateController();
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Thinking), Ms(0));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working), Ms(100));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Waiting), Ms(200));

        Assert.True(pet.Evaluate(Ms(499)).NextChangeAt <= Ms(500));
        Assert.Equal("think", pet.Player.CurrentState);

        // think settles after its 90 ms enter frame; its 1480 ms dot loop wraps at 1570 ms.
        var frame = pet.Evaluate(Ms(500));
        Assert.Equal("think", pet.Player.CurrentState);
        Assert.True(frame.NextChangeAt <= Ms(1570));
        pet.Evaluate(Ms(1569));
        Assert.Equal("think", pet.Player.CurrentState);

        pet.Evaluate(Ms(1570));
        Assert.Equal("notice", pet.Player.CurrentState);
    }

    [Fact]
    public void Working_finishes_its_tap_burst_before_switching()
    {
        var pet = CreateController();
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working), Ms(0));

        // Presses at 400, 580 and 760 ms, each 90 ms; the burst ends at 850 ms.
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Thinking), Ms(600));
        pet.Evaluate(Ms(849));
        Assert.Equal("working", pet.Player.CurrentState);

        pet.Evaluate(Ms(850));
        Assert.Equal("think", pet.Player.CurrentState);
    }

    [Theory]
    [InlineData(ClaudeStatus.Idle)]
    [InlineData(ClaudeStatus.Waiting)]
    public void Tool_failure_is_not_shown_outside_think_and_working(ClaudeStatus status)
    {
        var pet = CreateController();
        pet.ApplySnapshot(Snapshot(status), Ms(0));
        pet.ApplyEvent(Event(ClaudeStatus.ToolFailure), Ms(100));

        Assert.DoesNotContain(pet.Evaluate(Ms(100)).Sprites, s => s.File == "fx_sweat.png");
    }

    [Fact]
    public void Error_holds_until_the_next_thinking_snapshot()
    {
        var pet = CreateController();
        pet.ApplyEvent(Event(ClaudeStatus.Error), Ms(0));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Idle), Ms(0));

        pet.Evaluate(Ms(60_000));
        Assert.Equal("error", pet.Player.CurrentState);

        // error's 440 ms loop wraps next at 61 160 ms.
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Thinking), Ms(61_000));
        Assert.Equal("error", pet.Player.CurrentState);
        pet.Evaluate(Ms(61_160));
        Assert.Equal("think", pet.Player.CurrentState);
    }

    [Fact]
    public void Error_does_not_fall_asleep()
    {
        var pet = CreateController();
        pet.ApplyEvent(Event(ClaudeStatus.Error), Ms(0));

        pet.Evaluate(SleepAfter + Ms(1));
        Assert.Equal("error", pet.Player.CurrentState);
    }

    [Fact]
    public void Idle_falls_asleep_after_the_configured_time_and_wakes_on_activity()
    {
        var pet = CreateController();

        var frame = pet.Evaluate(Ms(0));
        Assert.Equal("idle", pet.Player.CurrentState);
        Assert.True(frame.NextChangeAt <= SleepAfter);

        Assert.True(pet.Evaluate(SleepAfter - Ms(1)).NextChangeAt <= SleepAfter);
        Assert.Equal("idle", pet.Player.CurrentState);

        pet.Evaluate(SleepAfter);
        Assert.Equal("sleep", pet.Player.CurrentState);

        // Woken on a wrap of sleep's 2100 ms loop, so think shows at once.
        var wake = SleepAfter + Ms(2100);
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Thinking), wake);
        Assert.Equal("think", pet.Player.CurrentState);

        // Back to idle: shown when think's loop wraps (90 ms enter + 1480 ms), the countdown starts over.
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Idle), wake + Ms(1000));
        pet.Evaluate(wake + Ms(1570));
        Assert.Equal("idle", pet.Player.CurrentState);
        pet.Evaluate(wake + Ms(1000) + SleepAfter - Ms(1));
        Assert.Equal("idle", pet.Player.CurrentState);
        pet.Evaluate(wake + Ms(1000) + SleepAfter);
        Assert.Equal("sleep", pet.Player.CurrentState);
    }

    [Fact]
    public void Tool_failure_plays_the_reaction_without_changing_state()
    {
        var pet = CreateController();
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working), Ms(0));
        pet.ApplyEvent(Event(ClaudeStatus.ToolFailure), Ms(100));

        var frame = pet.Evaluate(Ms(100));
        Assert.Equal("working", pet.Player.CurrentState);
        Assert.Contains(frame.Sprites, s => s.File == "fx_sweat.png");
    }

    [Fact]
    public void Pace_changes_the_tap_rhythm_without_leaving_working()
    {
        var pet = CreateController();
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working), Ms(0));
        Assert.Equal("active", pet.Player.Pace);

        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working, WorkPace.Composing), Ms(100));
        pet.ApplyEvent(Event(ClaudeStatus.ToolFailure), Ms(100));

        Assert.Equal("working", pet.Player.CurrentState);
        Assert.Equal("composing", pet.Player.Pace);
    }

    [Fact]
    public void Disconnect_returns_to_idle()
    {
        var pet = CreateController();
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working), Ms(0));
        pet.ApplyDisconnected(Ms(600));

        // After the tap burst that ends at 850 ms.
        pet.Evaluate(Ms(850));
        Assert.Equal("idle", pet.Player.CurrentState);
    }

    [Fact]
    public void Missing_required_state_is_rejected()
    {
        var manifest = TestAssets.LoadManifest();
        var withoutSleep = manifest with
        {
            States = manifest.States.Where(s => s.Key != "sleep").ToDictionary(s => s.Key, s => s.Value),
        };

        var ex = Assert.Throws<InvalidDataException>(() => CreateController(withoutSleep));
        Assert.Contains("'sleep'", ex.Message);
    }
}
