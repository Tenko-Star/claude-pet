using DeskPet.App.Animation;

namespace DeskPet.App.Tests;

public class BlinkSchedulerTests
{
    private static readonly BlinkLayer Eyes = new(
        "eyes",
        [new BlinkStep("half", Ms(60)), new BlinkStep("closed", Ms(90)), new BlinkStep("half", Ms(60))],
        Ms(2500),
        Ms(5500));

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    private static Func<TimeSpan, TimeSpan, TimeSpan> Fixed(params int[] intervals)
    {
        var queue = new Queue<int>(intervals);
        return (_, _) => Ms(queue.Count > 1 ? queue.Dequeue() : queue.Peek());
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(2999, null)]
    [InlineData(3000, "half")]
    [InlineData(3059, "half")]
    [InlineData(3060, "closed")]
    [InlineData(3149, "closed")]
    [InlineData(3150, "half")]
    [InlineData(3209, "half")]
    [InlineData(3210, null)]
    public void Plays_steps_at_their_boundaries(int now, string? expected)
    {
        var scheduler = new BlinkScheduler(Eyes, Fixed(3000));
        Assert.Equal(expected, scheduler.FileAt(Ms(now)));
    }

    [Fact]
    public void Next_blink_starts_one_interval_after_the_previous_one_ends()
    {
        var scheduler = new BlinkScheduler(Eyes, Fixed(3000, 4000));

        Assert.Equal("half", scheduler.FileAt(Ms(3000)));
        Assert.Null(scheduler.FileAt(Ms(3210)));
        Assert.Equal(Ms(3210 + 4000), scheduler.NextBlinkAt);
        Assert.Equal("half", scheduler.FileAt(Ms(7210)));
    }

    [Fact]
    public void NextChange_points_at_the_next_step_boundary()
    {
        var scheduler = new BlinkScheduler(Eyes, Fixed(3000));

        Assert.Equal(Ms(3000), scheduler.NextChange(Ms(0)));
        Assert.Equal(Ms(3060), scheduler.NextChange(Ms(3000)));
        Assert.Equal(Ms(3150), scheduler.NextChange(Ms(3100)));
        Assert.Equal(Ms(3210), scheduler.NextChange(Ms(3200)));
        Assert.Equal(Ms(6210), scheduler.NextChange(Ms(3210)));
    }

    [Fact]
    public void Jumping_far_ahead_skips_missed_blinks_without_getting_stuck()
    {
        var scheduler = new BlinkScheduler(Eyes, Fixed(3000));

        Assert.Null(scheduler.FileAt(Ms(100_000)));
        Assert.True(scheduler.NextBlinkAt > Ms(100_000));
        Assert.True(scheduler.NextBlinkAt <= Ms(100_000 + 3000));
    }

    [Fact]
    public void Default_interval_stays_within_bounds()
    {
        for (var i = 0; i < 10_000; i++)
        {
            var interval = BlinkScheduler.UniformInterval(Eyes.MinInterval, Eyes.MaxInterval);
            Assert.InRange(interval, Eyes.MinInterval, Eyes.MaxInterval);
        }
    }

    [Fact]
    public void Start_offset_delays_the_first_blink()
    {
        var scheduler = new BlinkScheduler(Eyes, Fixed(3000), start: Ms(500));
        Assert.Equal(Ms(3500), scheduler.NextBlinkAt);
    }
}
