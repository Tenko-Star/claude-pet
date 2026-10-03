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
/// snapshot (the next prompt); a ToolFailure event plays the <c>toolFailure</c> reaction.
/// Resting in idle for <c>sleepAfter</c> shows <c>sleep</c>.
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

    private static readonly string[] RequiredStates = [Idle, Think, Working, Notice, Done, Error, Sleep];

    private readonly SpritePlayer _player;
    private readonly TimeSpan _sleepAfter;

    private ClaudeStatus _status = ClaudeStatus.Idle;
    private bool _playingDone;
    private bool _errorHeld;
    private TimeSpan? _idleSince;

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

    public bool IsTalking => _player.IsTalking;

    public void SetTalking(bool talking, TimeSpan now) => _player.SetTalking(talking, now);

    public void ApplySnapshot(StatusSnapshot snapshot, TimeSpan now)
    {
        _status = snapshot.Status;
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
                break;
            case ClaudeStatus.Error:
                _playingDone = false;
                _errorHeld = true;
                break;
            case ClaudeStatus.ToolFailure:
                _player.TriggerReaction(ToolFailureReaction, now);
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
        if (_playingDone && _player.CurrentState != Done)
        {
            // The manifest's durationMs/then ended done; continue with the current status instead.
            _playingDone = false;
            Update(now);
            frame = _player.Evaluate(now);
        }

        if (_idleSince is { } since && _player.CurrentState == Idle)
        {
            var sleepAt = since + _sleepAfter;
            return frame.NextChangeAt > sleepAt ? frame with { NextChangeAt = sleepAt } : frame;
        }
        return frame;
    }

    private void Update(TimeSpan now) => _player.SetState(Resolve(now), now);

    private string Resolve(TimeSpan now)
    {
        if (_playingDone)
        {
            _idleSince = null;
            return Done;
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
