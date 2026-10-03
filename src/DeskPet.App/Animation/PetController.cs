using System.IO;
using StatusHub.Contracts;

namespace DeskPet.App.Animation;

/// <summary>
/// Maps the StatusHub stream onto manifest states and drives the <see cref="SpritePlayer"/>.
/// Pure like the player: time only comes in through <c>now</c> arguments.
/// </summary>
/// <remarks>
/// Snapshot statuses map to idle/think/working/notice. A Done event plays <c>done</c> until the
/// manifest's own <c>then</c> ends it; an Error event holds <c>error</c> until the next Thinking
/// snapshot (the next prompt); a ToolFailure event plays the <c>toolFailure</c> reaction, but only
/// while think or working is shown. Resting in idle for <c>sleepAfter</c> shows <c>sleep</c>.
/// <para>
/// Every state is shown for at least <see cref="MinStateDwell"/>, and done for at least
/// <see cref="DoneLock"/>; after that the switch still waits until the shown state's animation
/// cycle completes (<see cref="SpritePlayer.CycleComplete"/>). Input arriving meanwhile only
/// updates the target, so a quick run of statuses shows just the last one. After the lock, done gives way to think, working or notice;
/// idle never cuts it short. Another Done event restarts done's lock and its duration.
/// </para> The snapshot's pace selects the tap
/// rhythm (<c>active</c> / <c>composing</c>) without restarting the state.
/// </remarks>
public sealed class PetController
{
    public const string Idle = "idle";
    public const string Think = "think";
    public const string Working = "working";
    public const string Notice = "notice";
    public const string Done = "done";
    public const string Error = "error";
    public const string Sleep = "sleep";
    public const string ToolFailureReaction = "toolFailure";
    public const string ActivePace = "active";
    public const string ComposingPace = "composing";

    /// <summary>Shortest time any state stays on screen before switching to another.</summary>
    public static readonly TimeSpan MinStateDwell = TimeSpan.FromMilliseconds(500);

    /// <summary>Shortest time done stays on screen; only then can a new status replace it.</summary>
    public static readonly TimeSpan DoneLock = TimeSpan.FromSeconds(2);

    private static readonly string[] RequiredStates = [Idle, Think, Working, Notice, Done, Error, Sleep];

    private readonly SpritePlayer _player;
    private readonly TimeSpan _sleepAfter;

    private ClaudeStatus _status = ClaudeStatus.Idle;
    private WorkPace _pace = WorkPace.Active;
    private bool _playingDone;
    private bool _errorHeld;
    private TimeSpan? _idleSince;
    // When the shown state was entered; null until this controller has switched the player.
    private TimeSpan? _shownSince;
    // When done was shown or last restarted; null while done is not on screen.
    private TimeSpan? _doneSince;
    // When the target first differed from the shown state; null while they match.
    private TimeSpan? _pendingSince;
    // A held switch should be checked again at this time.
    private TimeSpan? _switchAt;

    public PetController(SpritePlayer player, TimeSpan sleepAfter)
    {
        foreach (var state in RequiredStates)
        {
            if (!player.Manifest.States.ContainsKey(state))
            {
                throw new InvalidDataException($"manifest: state '{state}' is required.");
            }
        }
        _player = player;
        _sleepAfter = sleepAfter;
    }

    public SpritePlayer Player => _player;

    /// <summary>
    /// Takes over the status of the controller of a previously shown character, so switching
    /// characters keeps the current status, a held error and the sleep countdown.
    /// A done animation in progress is not carried over.
    /// </summary>
    public void ContinueFrom(PetController previous, TimeSpan now)
    {
        _status = previous._status;
        _pace = previous._pace;
        _errorHeld = previous._errorHeld;
        _idleSince = previous._idleSince;
        Update(now);
    }

    public void ApplySnapshot(StatusSnapshot snapshot, TimeSpan now)
    {
        _status = snapshot.Status;
        _pace = snapshot.Pace;
        if (_status == ClaudeStatus.Thinking)
        {
            _errorHeld = false;
        }
        Update(now);
    }

    public void ApplyEvent(StatusEvent statusEvent, TimeSpan now)
    {
        switch (statusEvent.Kind)
        {
            case ClaudeStatus.Done:
                _errorHeld = false;
                _playingDone = true;
                if (_player.CurrentState == Done)
                {
                    _player.RestartDuration(now);
                    _shownSince = now;
                    _doneSince = now;
                }
                break;
            case ClaudeStatus.Error:
                _playingDone = false;
                _errorHeld = true;
                break;
            case ClaudeStatus.ToolFailure:
                if (_player.CurrentState is Think or Working)
                {
                    _player.TriggerReaction(ToolFailureReaction, now);
                }
                return;
            default:
                return;
        }
        Update(now);
    }

    /// <summary>The status stream is gone; show idle rather than a status that may be stale.</summary>
    public void ApplyDisconnected(TimeSpan now)
    {
        _status = ClaudeStatus.Idle;
        Update(now);
    }

    public FrameState Evaluate(TimeSpan now)
    {
        Update(now);
        var frame = _player.Evaluate(now);
        if (_doneSince is not null && _player.CurrentState != Done)
        {
            // The manifest's durationMs/then ended done; continue with the current status instead.
            _playingDone = false;
            _doneSince = null;
            _pendingSince = null;
            _shownSince = now;
            Update(now);
            frame = _player.Evaluate(now);
        }

        if (_switchAt is { } switchAt && switchAt > now)
        {
            frame = WakeBy(frame, switchAt);
        }
        if (_playingDone && _doneSince is { } doneSince && doneSince + DoneLock > now)
        {
            frame = WakeBy(frame, doneSince + DoneLock);
        }
        if (_idleSince is { } since && _player.CurrentState == Idle)
        {
            frame = WakeBy(frame, since + _sleepAfter);
        }
        return frame;
    }

    private static FrameState WakeBy(FrameState frame, TimeSpan at) =>
        frame.NextChangeAt > at ? frame with { NextChangeAt = at } : frame;

    private void Update(TimeSpan now)
    {
        _player.SetPace(_pace == WorkPace.Composing ? ComposingPace : ActivePace);
        _switchAt = null;

        var target = Resolve(now);
        var current = _player.CurrentState;
        if (target == current)
        {
            _pendingSince = null;
            return;
        }
        _pendingSince ??= now;
        if (_shownSince is { } shownSince)
        {
            var minimum = shownSince + (current == Done ? DoneLock : MinStateDwell);
            var due = minimum > _pendingSince.Value ? minimum : _pendingSince.Value;
            if (now < due)
            {
                _switchAt = due;
                return;
            }
            if (!_player.CycleComplete(due, now, out var wakeAt))
            {
                _switchAt = wakeAt;
                return;
            }
        }

        _player.SetState(target, now);
        if (_player.CurrentState != current)
        {
            _pendingSince = null;
            _shownSince = now;
            _doneSince = _player.CurrentState == Done ? now : null;
        }
    }

    private string Resolve(TimeSpan now)
    {
        if (_playingDone)
        {
            var locked = _doneSince is not { } doneSince || now - doneSince < DoneLock;
            if (locked || _status is not (ClaudeStatus.Thinking or ClaudeStatus.Working or ClaudeStatus.Waiting))
            {
                _idleSince = null;
                return Done;
            }
            _playingDone = false;
        }
        if (_errorHeld)
        {
            _idleSince = null;
            return Error;
        }

        var state = _status switch
        {
            ClaudeStatus.Thinking => Think,
            ClaudeStatus.Working => Working,
            ClaudeStatus.Waiting => Notice,
            _ => Idle,
        };
        if (state != Idle)
        {
            _idleSince = null;
            return state;
        }

        _idleSince ??= now;
        return now - _idleSince.Value >= _sleepAfter ? Sleep : Idle;
    }
}
