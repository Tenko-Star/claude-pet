using System.IO;
using Microsoft.Extensions.Logging;

namespace DeskPet.App.Animation;

/// <summary>
/// A sprite drawn at a stage position. With <see cref="AlignBottom"/> the sprite's bottom edge
/// sits at <see cref="Y"/> (effect sprites); otherwise its top edge does.
/// </summary>
public sealed record SpritePlacement(string File, int X, int Y, bool AlignBottom = false);

/// <summary>Sprites to draw bottom to top, and when the selection can next change.</summary>
public sealed record FrameState(IReadOnlyList<SpritePlacement> Sprites, TimeSpan NextChangeAt);

/// <summary>
/// Manifest-driven animation state. Pure: all time comes in through <c>now</c> arguments, which must
/// not decrease, and randomness through injectable functions, so frames are deterministic in tests.
/// </summary>
public sealed class SpritePlayer
{
    public const string DefaultInitialState = "idle";

    private readonly SpriteManifest _manifest;
    private readonly ILogger<SpritePlayer> _logger;
    private readonly Func<TimeSpan, TimeSpan, TimeSpan> _pickInterval;
    private readonly Func<int, int, int> _pickCount;
    private readonly BlinkScheduler _blink;

    private StateSpec _state;
    private TimeSpan _stateStart;
    // Exit frames of the previous state followed by enter frames of the current one.
    private IReadOnlyList<BodyFrame> _transition = [];
    private TimeSpan _settledAt;

    private int _hairIndex;
    private TimeSpan _hairNext;

    private bool _tapDown;
    private int _tapsLeft;
    private TimeSpan _tapNext;

    private ReactionSpec? _reaction;
    private TimeSpan _reactionStart;

    private TimeSpan? _talkingSince;

    /// <param name="pickInterval">Returns a duration in [min, max]; defaults to a uniform random pick.</param>
    /// <param name="pickCount">Returns an integer in [min, max]; defaults to a uniform random pick.</param>
    /// <param name="start">Time the player starts; the hair and blink timers count from here.</param>
    public SpritePlayer(
        SpriteManifest manifest,
        ILogger<SpritePlayer> logger,
        Func<TimeSpan, TimeSpan, TimeSpan>? pickInterval = null,
        TimeSpan start = default,
        Func<int, int, int>? pickCount = null,
        string initialState = DefaultInitialState)
    {
        _manifest = manifest;
        _logger = logger;
        _pickInterval = pickInterval ?? BlinkScheduler.UniformInterval;
        _pickCount = pickCount ?? ((min, max) => Random.Shared.Next(min, max + 1));
        _blink = new BlinkScheduler(manifest.Blink, _pickInterval, start);
        _state = manifest.States.TryGetValue(initialState, out var state)
            ? state
            : throw new InvalidDataException($"manifest: initial state '{initialState}' is missing.");
        _stateStart = start;
        _settledAt = start;
        _hairNext = start + HairFrameDuration;
        StartTap(start);
    }

    public SpriteManifest Manifest => _manifest;

    public string CurrentState => _state.Name;

    public bool IsTalking => _talkingSince is not null;

    /// <summary>
    /// Switches to <paramref name="name"/>: plays the current state's exit frames, then the new state's
    /// enter frames, then rests on its body. Effects and taps start once the body is reached.
    /// Unknown names are logged and ignored; the current state is a no-op.
    /// </summary>
    public void SetState(string name, TimeSpan now)
    {
        if (name == _state.Name)
        {
            return;
        }
        if (!_manifest.States.TryGetValue(name, out var next))
        {
            _logger.LogWarning("Unknown state '{State}', staying in '{Current}'.", name, _state.Name);
            return;
        }

        _transition = [.. _state.Exit, .. next.Enter];
        _state = next;
        _stateStart = now;
        _settledAt = now + _transition.Aggregate(TimeSpan.Zero, (sum, f) => sum + f.Duration);
        StartTap(_settledAt);
    }

    /// <summary>Plays a reaction overlay once from <paramref name="now"/>. Unknown names are logged and ignored.</summary>
    public void TriggerReaction(string name, TimeSpan now)
    {
        if (!_manifest.Reactions.TryGetValue(name, out var reaction))
        {
            _logger.LogWarning("Unknown reaction '{Reaction}'.", name);
            return;
        }
        _reaction = reaction;
        _reactionStart = now;
    }

    /// <summary>Turns the talk toggle on or off. The mouth starts open at <paramref name="now"/>.</summary>
    public void SetTalking(bool talking, TimeSpan now)
    {
        if (talking == IsTalking)
        {
            return;
        }
        _talkingSince = talking ? now : null;
    }

    public FrameState Evaluate(TimeSpan now)
    {
        FollowTimedStates(now);
        AdvanceHair(now);
        AdvanceTap(now);

        var sprites = new List<SpritePlacement>();
        var next = _hairNext;
        if (_state.Duration is { } duration)
        {
            next = Min(next, _stateStart + duration);
        }

        var offset = _manifest.CharacterOffset;
        foreach (var layer in _manifest.Layers)
        {
            switch (layer)
            {
                case ManifestLayers.Hair:
                    sprites.Add(new SpritePlacement(_manifest.Hair.Frames[_hairIndex], offset.X, offset.Y));
                    break;

                case ManifestLayers.Body:
                    sprites.Add(new SpritePlacement(BodyAt(now, ref next), offset.X, offset.Y));
                    break;

                case ManifestLayers.Eyes when _state.Eyes == EyesMode.Blink:
                    if (_blink.FileAt(now) is { } blinkFile)
                    {
                        sprites.Add(new SpritePlacement(blinkFile, offset.X, offset.Y));
                    }
                    next = Min(next, _blink.NextChange(now));
                    break;

                case ManifestLayers.Eyes when _state.EyesFile is { } eyesFile:
                    sprites.Add(new SpritePlacement(eyesFile, offset.X, offset.Y));
                    break;

                case ManifestLayers.Mouth when _state.Mouth && _talkingSince is { } since:
                {
                    var toggle = _manifest.Mouth.ToggleInterval;
                    var index = (now < since ? TimeSpan.Zero : now - since).Ticks / toggle.Ticks;
                    if (index % 2 == 0)
                    {
                        sprites.Add(new SpritePlacement(_manifest.Mouth.OpenFile, offset.X, offset.Y));
                    }
                    next = Min(next, since + TimeSpan.FromTicks((index + 1) * toggle.Ticks));
                    break;
                }

                case ManifestLayers.Fx:
                    AddStateFx(now, sprites, ref next);
                    AddReaction(now, sprites, ref next);
                    break;
            }
        }

        return new FrameState(sprites, next);
    }

    private TimeSpan HairFrameDuration => _state.HairFrameDuration ?? _manifest.Hair.FrameDuration;

    // Applies durationMs/then switches whose deadline has passed, at their exact deadline.
    private void FollowTimedStates(TimeSpan now)
    {
        // Bounded so a manifest cycle of timed states cannot spin forever.
        for (var i = 0; i < _manifest.States.Count; i++)
        {
            if (_state.Duration is not { } duration || _state.Then is not { } then || now < _stateStart + duration)
            {
                return;
            }
            SetState(then, _stateStart + duration);
        }
    }

    private void AdvanceHair(TimeSpan now)
    {
        while (now >= _hairNext)
        {
            _hairIndex = (_hairIndex + 1) % _manifest.Hair.Frames.Count;
            _hairNext += HairFrameDuration;
        }
    }

    private void StartTap(TimeSpan from)
    {
        _tapDown = false;
        _tapsLeft = 0;
        if (_state.Tap is { } tap)
        {
            _tapNext = from + _pickInterval(tap.Pause.Min, tap.Pause.Max);
        }
    }

    // Same rhythm as the asset demo: a burst of presses separated by gaps, then a pause.
    private void AdvanceTap(TimeSpan now)
    {
        if (_state.Tap is not { } tap)
        {
            return;
        }
        while (now >= _tapNext)
        {
            if (_tapDown)
            {
                _tapDown = false;
                var wait = _tapsLeft > 0 ? tap.Gap : tap.Pause;
                _tapNext += _pickInterval(wait.Min, wait.Max);
            }
            else
            {
                if (_tapsLeft <= 0)
                {
                    _tapsLeft = _pickCount(tap.BurstMin, tap.BurstMax);
                }
                _tapsLeft--;
                _tapDown = true;
                _tapNext += tap.Down;
            }
        }
    }

    private string BodyAt(TimeSpan now, ref TimeSpan next)
    {
        if (now < _settledAt)
        {
            var end = _stateStart;
            foreach (var frame in _transition)
            {
                end += frame.Duration;
                if (now < end)
                {
                    next = Min(next, end);
                    return frame.File;
                }
            }
        }

        if (_state.Tap is { } tap)
        {
            next = Min(next, _tapNext);
            return _tapDown ? tap.Frame : _state.Body;
        }
        return _state.Body;
    }

    private void AddStateFx(TimeSpan now, List<SpritePlacement> sprites, ref TimeSpan next)
    {
        if (_state.Fx is not { } fx)
        {
            return;
        }
        if (now < _settledAt)
        {
            next = Min(next, _settledAt);
            return;
        }

        var elapsed = now - _settledAt;
        switch (fx)
        {
            case LoopFx loop:
            {
                var cycle = FxFrame.TotalDuration(loop.Frames);
                var cycleStart = _settledAt + TimeSpan.FromTicks(elapsed.Ticks / cycle.Ticks * cycle.Ticks);
                AddFrameAt(loop.Frames, loop.Anchor, cycleStart, now, sprites, ref next);
                break;
            }

            case PopFx pop:
            {
                var popDuration = FxFrame.TotalDuration(pop.Pop);
                var cycle = popDuration + pop.RepeatInterval;
                var cycleStart = _settledAt + TimeSpan.FromTicks(elapsed.Ticks / cycle.Ticks * cycle.Ticks);
                var inCycle = now - cycleStart;
                if (inCycle < popDuration)
                {
                    AddFrameAt(pop.Pop, pop.Anchor, cycleStart, now, sprites, ref next);
                    break;
                }

                // Hold: rest, then shift by bobPx every other bob interval, until the next pop.
                var hold = pop.Hold;
                var holdElapsed = inCycle - popDuration;
                var bobIndex = holdElapsed.Ticks / hold.BobInterval.Ticks;
                var bob = bobIndex % 2 == 1 ? hold.BobPx : 0;
                sprites.Add(new SpritePlacement(hold.File, pop.Anchor.X, pop.Anchor.Y + bob, AlignBottom: true));
                var bobNext = cycleStart + popDuration + TimeSpan.FromTicks((bobIndex + 1) * hold.BobInterval.Ticks);
                next = Min(next, Min(bobNext, cycleStart + cycle));
                break;
            }
        }
    }

    private void AddReaction(TimeSpan now, List<SpritePlacement> sprites, ref TimeSpan next)
    {
        if (_reaction is not { } reaction)
        {
            return;
        }
        if (now >= _reactionStart + FxFrame.TotalDuration(reaction.Frames))
        {
            _reaction = null;
            return;
        }
        AddFrameAt(reaction.Frames, reaction.Anchor, _reactionStart, now, sprites, ref next);
    }

    // Adds the frame of a sequence that started at sequenceStart and is showing at now.
    private static void AddFrameAt(
        IReadOnlyList<FxFrame> frames, PixelPoint anchor, TimeSpan sequenceStart, TimeSpan now,
        List<SpritePlacement> sprites, ref TimeSpan next)
    {
        var end = sequenceStart;
        foreach (var frame in frames)
        {
            end += frame.Duration;
            if (now < end)
            {
                foreach (var sprite in frame.Sprites)
                {
                    sprites.Add(new SpritePlacement(sprite.File, anchor.X + sprite.Dx, anchor.Y + sprite.Dy, AlignBottom: true));
                }
                next = Min(next, end);
                return;
            }
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
