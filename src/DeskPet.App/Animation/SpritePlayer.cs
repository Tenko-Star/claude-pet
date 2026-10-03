using Microsoft.Extensions.Logging;

namespace DeskPet.App.Animation;

/// <summary>Sprite files to draw bottom to top, and when the selection can next change.</summary>
public sealed record FrameState(IReadOnlyList<string> Files, TimeSpan NextChangeAt);

/// <summary>
/// Manifest-driven animation state. Pure: all time comes in through <c>now</c> arguments,
/// so frame selection is deterministic and testable without a window.
/// </summary>
public sealed class SpritePlayer
{
    public const string IdleStatus = "idle";

    private static readonly HashSet<string> SupportedStatuses = new(StringComparer.Ordinal) { IdleStatus };

    private readonly SpriteManifest _manifest;
    private readonly ILogger<SpritePlayer> _logger;
    private readonly Dictionary<BlinkLayer, BlinkScheduler> _blinks = [];
    private TimeSpan? _talkingSince;

    public SpritePlayer(
        SpriteManifest manifest,
        ILogger<SpritePlayer> logger,
        Func<TimeSpan, TimeSpan, TimeSpan>? blinkInterval = null,
        TimeSpan start = default)
    {
        _manifest = manifest;
        _logger = logger;
        foreach (var blink in manifest.Layers.OfType<BlinkLayer>())
        {
            _blinks[blink] = new BlinkScheduler(blink, blinkInterval, start);
        }
    }

    public SpriteManifest Manifest => _manifest;

    public string CurrentStatus { get; private set; } = IdleStatus;

    public bool IsTalking => _talkingSince is not null;

    /// <summary>
    /// Extension point for status mapping. Only <c>idle</c> exists today; anything else
    /// falls back to idle and is logged.
    /// </summary>
    public void SetStatus(string status)
    {
        if (SupportedStatuses.Contains(status))
        {
            CurrentStatus = status;
            return;
        }
        _logger.LogWarning("Unknown status '{Status}', falling back to '{Fallback}'.", status, IdleStatus);
        CurrentStatus = IdleStatus;
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
        var files = new List<string>(_manifest.Layers.Count);
        var next = TimeSpan.MaxValue;

        foreach (var layer in _manifest.Layers)
        {
            switch (layer)
            {
                case StaticLayer s:
                    files.Add(s.File);
                    break;

                case LoopLayer loop:
                {
                    var index = now.Ticks / loop.FrameDuration.Ticks;
                    files.Add(loop.Frames[(int)(index % loop.Frames.Count)]);
                    next = Min(next, TimeSpan.FromTicks((index + 1) * loop.FrameDuration.Ticks));
                    break;
                }

                case BlinkLayer blink:
                {
                    var scheduler = _blinks[blink];
                    if (scheduler.FileAt(now) is { } file)
                    {
                        files.Add(file);
                    }
                    next = Min(next, scheduler.NextChange(now));
                    break;
                }

                case TalkLayer talk when _talkingSince is { } since:
                {
                    var elapsed = now < since ? TimeSpan.Zero : now - since;
                    var index = elapsed.Ticks / talk.ToggleInterval.Ticks;
                    if (index % 2 == 0)
                    {
                        files.Add(talk.OpenFile);
                    }
                    next = Min(next, since + TimeSpan.FromTicks((index + 1) * talk.ToggleInterval.Ticks));
                    break;
                }

                case TalkLayer:
                    break;

                default:
                    throw new NotSupportedException($"Layer kind {layer.GetType().Name} is not supported.");
            }
        }

        return new FrameState(files, next);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
