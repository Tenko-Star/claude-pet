using DeskPet.App.Animation;
using StatusHub.Contracts;

namespace DeskPet.App.Tests;

public class PetControllerTests
{
    private static readonly TimeSpan SleepAfter = TimeSpan.FromMinutes(5);

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static PetController CreateController(SpriteManifest? manifest = null) =>
        new(new SpritePlayer(manifest ?? TestAssets.LoadManifest(), new RecordingLogger<SpritePlayer>(), (min, _) => min), SleepAfter);

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
    public void Done_plays_for_its_duration_then_follows_the_latest_snapshot()
    {
        var pet = CreateController();
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Thinking), Ms(0));
        pet.ApplyEvent(Event(ClaudeStatus.Done), Ms(1000));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Working), Ms(1000));

        pet.Evaluate(Ms(3199));
        Assert.Equal("done", pet.Player.CurrentState);

        // done lasts 2200 ms from the event, including think's 90 ms exit frame.
        pet.Evaluate(Ms(3200));
        Assert.Equal("working", pet.Player.CurrentState);
    }

    [Fact]
    public void Error_holds_until_the_next_thinking_snapshot()
    {
        var pet = CreateController();
        pet.ApplyEvent(Event(ClaudeStatus.Error), Ms(0));
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Idle), Ms(0));

        pet.Evaluate(Ms(60_000));
        Assert.Equal("error", pet.Player.CurrentState);

        pet.ApplySnapshot(Snapshot(ClaudeStatus.Thinking), Ms(61_000));
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

        var wake = SleepAfter + Ms(1000);
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Thinking), wake);
        Assert.Equal("think", pet.Player.CurrentState);

        // Back to idle: the countdown starts over.
        pet.ApplySnapshot(Snapshot(ClaudeStatus.Idle), wake + Ms(1000));
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
        pet.ApplyDisconnected(Ms(100));

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
