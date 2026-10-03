namespace DeskPet.App.Animation;

/// <summary>
/// Plays a <see cref="BlinkLayer"/> sequence at random intervals. Time only moves forward:
/// calls must pass non-decreasing <c>now</c> values.
/// </summary>
public sealed class BlinkScheduler
{
    private readonly BlinkLayer _layer;
    private readonly Func<TimeSpan, TimeSpan, TimeSpan> _nextInterval;
    private TimeSpan _sequenceStart;

    /// <param name="layer">Blink steps and interval bounds from the manifest.</param>
    /// <param name="nextInterval">Returns a gap in [min, max]; defaults to a uniform random pick.</param>
    /// <param name="start">Time the scheduler starts; the first blink begins one interval later.</param>
    public BlinkScheduler(BlinkLayer layer, Func<TimeSpan, TimeSpan, TimeSpan>? nextInterval = null, TimeSpan start = default)
    {
        _layer = layer;
        _nextInterval = nextInterval ?? UniformInterval;
        _sequenceStart = start + _nextInterval(layer.MinInterval, layer.MaxInterval);
    }

    /// <summary>Start time of the current or next blink sequence.</summary>
    public TimeSpan NextBlinkAt => _sequenceStart;

    /// <summary>The blink file to show at <paramref name="now"/>, or null when the eyes are open.</summary>
    public string? FileAt(TimeSpan now)
    {
        Advance(now);
        return StepAt(now, out _)?.File;
    }

    /// <summary>The earliest time after <paramref name="now"/> at which <see cref="FileAt"/> can change.</summary>
    public TimeSpan NextChange(TimeSpan now)
    {
        Advance(now);
        return StepAt(now, out var stepEnd) is null ? _sequenceStart : stepEnd;
    }

    public static TimeSpan UniformInterval(TimeSpan min, TimeSpan max) =>
        min + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * (max - min).TotalMilliseconds);

    // Skips past every sequence that has fully ended by now, scheduling the next one each time.
    private void Advance(TimeSpan now)
    {
        var duration = _layer.SequenceDuration;
        while (now >= _sequenceStart + duration)
        {
            _sequenceStart += duration + _nextInterval(_layer.MinInterval, _layer.MaxInterval);
        }
    }

    private BlinkStep? StepAt(TimeSpan now, out TimeSpan stepEnd)
    {
        stepEnd = _sequenceStart;
        if (now < _sequenceStart)
        {
            return null;
        }
        foreach (var step in _layer.Steps)
        {
            stepEnd += step.Duration;
            if (now < stepEnd)
            {
                return step;
            }
        }
        return null;
    }
}
