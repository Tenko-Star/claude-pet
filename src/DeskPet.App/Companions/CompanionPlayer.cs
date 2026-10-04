using DeskPet.App.Animation;
using StatusHub.Contracts;

namespace DeskPet.App.Companions;

/// <summary>One sprite or pixel to draw with its top-left corner at (X, Y) in pet-stage pixels.</summary>
public readonly record struct CompanionDraw(PixelBuffer Buffer, int X, int Y);

/// <summary>What to draw now, bottom to top, and when the picture changes next.</summary>
public sealed record CompanionFrameState(IReadOnlyList<CompanionDraw> Draws, TimeSpan NextChangeAt);

/// <summary>
/// Companions of the pet: an orange per active session and an orb per running subagent of a session.
/// Every snapshot's session list is diffed against what is shown: new entries spawn, missing ones finish with a happy
/// face and dissolve into pixels. A failed session shows its orange's error face until the session moves on.
/// Oranges and orbs share one state machine; time is injected, so the player is deterministic in tests.
/// </summary>
public sealed class CompanionPlayer
{
    // Timings in milliseconds.
    private const double OrangeFadeInMs = 250;
    private const double OrbTravelMs = 400;
    private const double LandStretchMs = 80;
    private const double LandSquashMs = 80;
    private const double BobStepMs = 500;
    private const double HopStretchMs = 100;
    private const double HopTopMs = 100;
    private const double HopSquashMs = 80;
    private const double CompleteMs = 700;
    private const double CompleteHopAtMs = 120;
    private const double ShakeMs = 300;
    private const double ShakeStepMs = 60;
    private const double DissolveMs = 600;
    private const double GlideMs = 120;

    // A finish or failure during the spawn skips the rest of the spawn and plays its phase this fast at most.
    private const double CoalescedExitMs = 250;
    private const double MaxTimeScale = 3;

    // Dissolve particle speeds in pixels per second.
    private const double OutwardMin = 6;
    private const double OutwardMax = 14;
    private const double UpwardMin = 14;
    private const double UpwardMax = 26;
    private const double Jitter = 4;

    private static readonly (double Min, double Max) HopEveryMs = (3000, 5000);
    private static readonly (double Min, double Max) SparkleMs = (200, 900);
    private static readonly double[] CompleteHopMs = [80, 100, 80, 80];
    private static readonly int[] CompleteHopHeights = [-1, -2, -1, 0]; // 0: squash on landing
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private static readonly (CompanionFrame Frame, double Ms)[] OrangeBlink =
        [(CompanionFrame.BlinkHalf, 60), (CompanionFrame.Blink, 60), (CompanionFrame.BlinkHalf, 60)];
    private static readonly (CompanionFrame Frame, double Ms)[] OrbBlink = [(CompanionFrame.Blink, 120)];
    private static readonly (double Min, double Max) OrangeBlinkEveryMs = (3000, 7000);
    private static readonly (double Min, double Max) OrbBlinkEveryMs = (3000, 6000);

    private readonly CompanionArt _art;
    private readonly Random _random;
    private readonly List<Companion> _companions = [];
    private readonly List<Particle> _particles = [];
    private CompanionLayout _layout;
    private TimeSpan _last;

    public CompanionPlayer(CompanionArt art, CompanionLayout layout, TimeSpan start, Random? random = null)
    {
        _art = art;
        _layout = layout;
        _last = start;
        _random = random ?? new Random();
    }

    private enum Phase
    {
        Spawning,
        Idle,
        Completing,
        Erroring,
        Error,
        Dissolving,
    }

    /// <summary>Moves every companion to the slots of another pet layout, for example after a character switch.</summary>
    public void SetLayout(CompanionLayout layout, TimeSpan now)
    {
        Advance(now);
        _layout = layout;
        AssignSlots();
    }

    /// <summary>Shows the sessions of the latest snapshot.</summary>
    public void Sync(IReadOnlyList<SessionInfo>? sessions, TimeSpan now)
    {
        Advance(now);
        sessions ??= [];
        var ids = sessions.Select(s => s.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var orange in Live().Where(c => c.IsOrange && !ids.Contains(c.Key)).ToList())
        {
            Finish(orange);
        }

        foreach (var session in sessions)
        {
            var orange = Live().FirstOrDefault(c => c.IsOrange && c.Key == session.SessionId);
            if (orange is null)
            {
                var index = FreeOrangeIndex();
                if (index < 0)
                {
                    continue; // More sessions than slots: the extra ones are not shown.
                }
                var slot = _layout.OrangeSlot(index);
                orange = Spawn(session.SessionId, null, index, slot.X, slot.Y);
            }

            if (session.Failed && orange.Phase is Phase.Spawning or Phase.Idle)
            {
                Fail(orange);
            }
            else if (!session.Failed && orange.Phase is Phase.Erroring or Phase.Error)
            {
                Enter(orange, Phase.Idle);
            }

            var agentIds = session.Subagents.Select(a => a.AgentId).ToHashSet(StringComparer.Ordinal);
            foreach (var orb in Live().Where(c => c.Parent == orange && !agentIds.Contains(c.Key)).ToList())
            {
                Finish(orb);
            }
            foreach (var agent in session.Subagents)
            {
                if (Live().Any(c => c.Parent == orange && c.Key == agent.AgentId))
                {
                    continue;
                }
                if (_companions.Count(c => c.Parent == orange && c.Phase != Phase.Dissolving) >= CompanionLayout.MaxOrbsPerOrange)
                {
                    break;
                }
                Spawn(agent.AgentId, orange, orange.Index, orange.X, orange.Y);
            }
        }
        AssignSlots();
    }

    /// <summary>The status stream was lost: every companion dissolves without a finishing face.</summary>
    public void Clear(TimeSpan now)
    {
        Advance(now);
        foreach (var companion in _companions.Where(c => c.Phase != Phase.Dissolving).ToList())
        {
            StartDissolve(companion);
        }
        AssignSlots();
    }

    public CompanionFrameState Evaluate(TimeSpan now)
    {
        Advance(now);
        var nowMs = now.TotalMilliseconds;
        var draws = new List<CompanionDraw>();
        foreach (var companion in _companions.Where(c => c.IsOrange).Concat(_companions.Where(c => !c.IsOrange)))
        {
            var pose = PoseOf(companion, nowMs);
            if (!pose.Visible)
            {
                continue;
            }
            var x = (int)Math.Round(companion.X);
            var y = (int)Math.Round(companion.Y);
            if (pose.Pixel)
            {
                draws.Add(new CompanionDraw(_art.OrbBright[companion.Index % _art.OrbBright.Count], x, y));
                continue;
            }
            var center = companion.IsOrange ? CompanionLayout.OrangeCenter : CompanionLayout.OrbCenter;
            var buffer = FrameOf(companion, pose.Frame);
            if (pose.Alpha < 1)
            {
                buffer = Faded(buffer, pose.Alpha);
            }
            draws.Add(new CompanionDraw(buffer, x + pose.Dx - center.X, y + pose.Dy - center.Y));
            if (!companion.IsOrange && companion.Phase is Phase.Idle or Phase.Completing)
            {
                var sparkle = _art.OrbSparkle[companion.Index % _art.OrbSparkle.Count];
                foreach (var s in companion.Sparkles.Where(s => s.On))
                {
                    draws.Add(new CompanionDraw(sparkle, x + s.X, y + s.Y));
                }
            }
        }
        foreach (var p in _particles)
        {
            draws.Add(new CompanionDraw(ColorPixel(p.Color, 1 - p.AgeMs / DissolveMs), (int)Math.Floor(p.X), (int)Math.Floor(p.Y)));
        }
        return new CompanionFrameState(draws, NextChangeAt(now));
    }

    private IEnumerable<Companion> Live() =>
        _companions.Where(c => c.Phase is not (Phase.Completing or Phase.Dissolving));

    private int FreeOrangeIndex()
    {
        for (var i = 0; i < CompanionLayout.MaxOranges; i++)
        {
            if (!_companions.Any(c => c.IsOrange && c.Index == i && c.Phase != Phase.Dissolving))
            {
                return i;
            }
        }
        return -1;
    }

    private Companion Spawn(string key, Companion? parent, int index, double x, double y)
    {
        var isOrange = parent is null;
        var companion = new Companion(key, parent, index)
        {
            X = x,
            Y = y,
            FromX = x,
            FromY = y,
            BobPhaseMs = _random.NextDouble() * BobStepMs * 2,
            BlinkInMs = Between(isOrange ? OrangeBlinkEveryMs : OrbBlinkEveryMs),
            HopInMs = Between(HopEveryMs),
        };
        if (!isOrange)
        {
            var count = _random.Next(3, 6);
            for (var k = 0; k < count; k++)
            {
                var angle = (k + 0.15 + _random.NextDouble() * 0.7) / count * Math.PI * 2;
                var distance = _random.Next(9, 11);
                companion.Sparkles.Add(new Sparkle
                {
                    X = (int)Math.Round(Math.Cos(angle) * distance),
                    Y = (int)Math.Round(Math.Sin(angle) * distance),
                    On = _random.NextDouble() < 0.5,
                    TimerMs = Between(SparkleMs),
                });
            }
        }
        _companions.Add(companion);
        return companion;
    }

    // Oranges keep their fixed slot; orbs fill the arcs grouped by orange, oldest first.
    private void AssignSlots()
    {
        foreach (var orange in _companions.Where(c => c.IsOrange))
        {
            orange.Slot = _layout.OrangeSlot(orange.Index);
        }
        var orbs = _companions.Where(c => !c.IsOrange && c.Phase != Phase.Dissolving).OrderBy(c => c.Index).ToList();
        var arc = _layout.ArcSlots(orbs.Count);
        for (var i = 0; i < orbs.Count; i++)
        {
            orbs[i].Slot = arc[i];
        }
    }

    private static void Enter(Companion companion, Phase phase, double timeScale = 1)
    {
        companion.Phase = phase;
        companion.PhaseMs = 0;
        companion.TimeScale = timeScale;
        companion.BlinkMs = -1;
        companion.HopMs = -1;
    }

    private static double CoalescedScale(double phaseMs) => Math.Min(MaxTimeScale, Math.Max(1, phaseMs / CoalescedExitMs));

    // A finished agent: happy face, hop, dissolve. An orange takes its orbs along so they all dissolve together.
    private void Finish(Companion companion)
    {
        if (companion.Phase is Phase.Erroring or Phase.Error)
        {
            DissolveWithOrbs(companion);
            return;
        }
        var scale = companion.Phase == Phase.Spawning ? CoalescedScale(CompleteMs) : 1;
        var family = new List<Companion> { companion };
        if (companion.IsOrange)
        {
            family.AddRange(_companions.Where(orb => orb.Parent == companion));
        }
        foreach (var member in family)
        {
            if (member.Phase is Phase.Spawning)
            {
                (member.X, member.Y) = (member.Slot.X, member.Slot.Y);
            }
            if (member.Phase is Phase.Spawning or Phase.Idle)
            {
                Enter(member, Phase.Completing, scale);
            }
        }
    }

    private static void Fail(Companion orange)
    {
        var scale = 1.0;
        if (orange.Phase == Phase.Spawning)
        {
            (orange.X, orange.Y) = (orange.Slot.X, orange.Slot.Y);
            scale = CoalescedScale(ShakeMs);
        }
        Enter(orange, Phase.Erroring, scale);
    }

    private void DissolveWithOrbs(Companion companion)
    {
        StartDissolve(companion);
        if (companion.IsOrange)
        {
            foreach (var orb in _companions.Where(c => c.Parent == companion && c.Phase != Phase.Dissolving).ToList())
            {
                StartDissolve(orb);
            }
        }
        AssignSlots();
    }

    // Every opaque pixel of the current frame becomes a particle drifting up and outward.
    private void StartDissolve(Companion companion)
    {
        var pose = PoseOf(companion, 0);
        var gray = companion.Phase is Phase.Erroring or Phase.Error;
        var cx = Math.Round(companion.X);
        var cy = Math.Round(companion.Y);
        if (pose.Pixel)
        {
            Emit(cx, cy, 0, -1, ColorOf(_art.OrbBright[companion.Index % _art.OrbBright.Count], 0));
        }
        else if (pose.Visible)
        {
            var center = companion.IsOrange ? CompanionLayout.OrangeCenter : CompanionLayout.OrbCenter;
            var buffer = FrameOf(companion, pose.Frame);
            for (var py = 0; py < buffer.Height; py++)
            {
                for (var px = 0; px < buffer.Width; px++)
                {
                    var i = (py * buffer.Width + px) * 4;
                    if (buffer.Pbgra[i + 3] == 0)
                    {
                        continue;
                    }
                    var color = ColorOf(buffer, i);
                    Emit(cx + pose.Dx - center.X + px, cy + pose.Dy - center.Y + py, px - center.X, py - center.Y,
                        gray ? CompanionArt.Gray(color) : color);
                }
            }
        }
        Enter(companion, Phase.Dissolving);
    }

    private void Emit(double x, double y, double rx, double ry, int color)
    {
        var length = Math.Max(1e-6, Math.Sqrt(rx * rx + ry * ry));
        var outward = OutwardMin + _random.NextDouble() * (OutwardMax - OutwardMin);
        _particles.Add(new Particle
        {
            X = x,
            Y = y,
            Vx = rx / length * outward + (_random.NextDouble() * 2 - 1) * Jitter,
            Vy = ry / length * outward * 0.5 - (UpwardMin + _random.NextDouble() * (UpwardMax - UpwardMin)),
            Color = color,
        });
    }

    private void Advance(TimeSpan now)
    {
        var dt = (now - _last).TotalMilliseconds;
        _last = now;
        if (dt <= 0)
        {
            return;
        }

        var glide = 1 - Math.Exp(-dt / GlideMs);
        foreach (var c in _companions)
        {
            c.PhaseMs += dt * c.TimeScale;
            foreach (var s in c.Sparkles)
            {
                if ((s.TimerMs -= dt) <= 0)
                {
                    s.On = !s.On;
                    s.TimerMs = Between(SparkleMs);
                }
            }
            if (c.Phase == Phase.Idle)
            {
                TickIdle(c, dt);
            }

            if (!c.IsOrange && c.Phase == Phase.Spawning && c.PhaseMs < OrbTravelMs)
            {
                var k = 1 - (1 - c.PhaseMs / OrbTravelMs) * (1 - c.PhaseMs / OrbTravelMs);
                c.X = c.FromX + (c.Slot.X - c.FromX) * k;
                c.Y = c.FromY + (c.Slot.Y - c.FromY) * k;
            }
            else if (c.Phase != Phase.Dissolving)
            {
                c.X = Math.Abs(c.Slot.X - c.X) < 0.5 ? c.Slot.X : c.X + (c.Slot.X - c.X) * glide;
                c.Y = Math.Abs(c.Slot.Y - c.Y) < 0.5 ? c.Slot.Y : c.Y + (c.Slot.Y - c.Y) * glide;
            }
        }

        foreach (var c in _companions.ToList())
        {
            if (c.Phase == Phase.Spawning && c.PhaseMs >= SpawnLeadMs(c) + LandStretchMs + LandSquashMs)
            {
                Enter(c, Phase.Idle);
            }
            else if (c.Phase == Phase.Erroring && c.PhaseMs >= ShakeMs)
            {
                Enter(c, Phase.Error);
            }
            else if (c.Phase == Phase.Completing && c.PhaseMs >= CompleteMs)
            {
                DissolveWithOrbs(c);
            }
        }
        _companions.RemoveAll(c => c.Phase == Phase.Dissolving && c.PhaseMs >= DissolveMs);

        foreach (var p in _particles)
        {
            p.AgeMs += dt;
            p.X += p.Vx * dt / 1000;
            p.Y += p.Vy * dt / 1000;
        }
        _particles.RemoveAll(p => p.AgeMs >= DissolveMs);
    }

    private void TickIdle(Companion c, double dt)
    {
        var blink = c.IsOrange ? OrangeBlink : OrbBlink;
        if (c.HopMs >= 0)
        {
            c.HopMs += dt;
            if (c.HopMs >= HopStretchMs + HopTopMs + HopSquashMs)
            {
                c.HopMs = -1;
                c.HopInMs = Between(HopEveryMs);
            }
        }
        else if ((c.HopInMs -= dt) <= 0 && c.BlinkMs < 0)
        {
            c.HopMs = 0;
        }

        if (c.BlinkMs >= 0)
        {
            c.BlinkMs += dt;
            if (c.BlinkMs >= blink.Sum(step => step.Ms))
            {
                c.BlinkMs = -1;
                c.BlinkInMs = Between(c.IsOrange ? OrangeBlinkEveryMs : OrbBlinkEveryMs);
            }
        }
        else if (c.HopMs < 0 && (c.BlinkInMs -= dt) <= 0)
        {
            c.BlinkMs = 0;
        }
    }

    // While anything moves, redraw every frame; otherwise wake for the next bob step, blink, hop or sparkle.
    private TimeSpan NextChangeAt(TimeSpan now)
    {
        if (_companions.Count == 0 && _particles.Count == 0)
        {
            return TimeSpan.MaxValue;
        }
        if (_particles.Count > 0 || _companions.Any(c =>
            c.Phase is Phase.Spawning or Phase.Completing or Phase.Erroring or Phase.Dissolving
            || c.HopMs >= 0 || c.BlinkMs >= 0 || c.X != c.Slot.X || c.Y != c.Slot.Y))
        {
            return now + FrameInterval;
        }

        var wait = double.MaxValue;
        foreach (var c in _companions.Where(c => c.Phase == Phase.Idle))
        {
            var bob = BobStepMs - (now.TotalMilliseconds + c.BobPhaseMs) % BobStepMs;
            wait = Math.Min(wait, Math.Min(bob, Math.Min(c.BlinkInMs, c.HopInMs)));
            foreach (var s in c.Sparkles)
            {
                wait = Math.Min(wait, s.TimerMs);
            }
        }
        return wait == double.MaxValue ? TimeSpan.MaxValue : now + TimeSpan.FromMilliseconds(Math.Max(1, wait));
    }

    private static double SpawnLeadMs(Companion c) => c.IsOrange ? OrangeFadeInMs : OrbTravelMs;

    private static Pose PoseOf(Companion c, double nowMs)
    {
        switch (c.Phase)
        {
            case Phase.Spawning:
                if (c.PhaseMs < SpawnLeadMs(c))
                {
                    return c.IsOrange
                        ? new Pose(CompanionFrame.Base, Alpha: c.PhaseMs / OrangeFadeInMs)
                        : new Pose(CompanionFrame.Base, Pixel: true);
                }
                return new Pose(c.PhaseMs - SpawnLeadMs(c) < LandStretchMs ? CompanionFrame.Stretch : CompanionFrame.Squash);
            case Phase.Idle:
                if (c.HopMs >= 0)
                {
                    return c.HopMs < HopStretchMs ? new Pose(CompanionFrame.Stretch, Dy: -1)
                        : c.HopMs < HopStretchMs + HopTopMs ? new Pose(CompanionFrame.Base, Dy: -1)
                        : new Pose(CompanionFrame.Squash);
                }
                var bob = Math.Floor((nowMs + c.BobPhaseMs) / BobStepMs) % 2 == 0 ? 0 : -1;
                return new Pose(BlinkFrame(c) ?? CompanionFrame.Base, Dy: bob);
            case Phase.Completing:
                return CompletePose(c.PhaseMs);
            case Phase.Erroring:
                return new Pose(CompanionFrame.Error, Dx: Math.Floor(c.PhaseMs / ShakeStepMs) % 2 == 0 ? 1 : -1);
            case Phase.Error:
                return new Pose(CompanionFrame.Error);
            default:
                return new Pose(CompanionFrame.Base, Visible: false);
        }
    }

    private static CompanionFrame? BlinkFrame(Companion c)
    {
        if (c.BlinkMs < 0)
        {
            return null;
        }
        var t = c.BlinkMs;
        foreach (var (frame, ms) in c.IsOrange ? OrangeBlink : OrbBlink)
        {
            if (t < ms)
            {
                return frame;
            }
            t -= ms;
        }
        return null;
    }

    // The happy face stays in the air: up 1, up 2, up 1, then squash on landing.
    private static Pose CompletePose(double phaseMs)
    {
        var t = phaseMs - CompleteHopAtMs;
        if (t < 0)
        {
            return new Pose(CompanionFrame.Happy);
        }
        foreach (var (ms, height) in CompleteHopMs.Zip(CompleteHopHeights))
        {
            if (t < ms)
            {
                return height == 0 ? new Pose(CompanionFrame.Squash) : new Pose(CompanionFrame.Happy, Dy: height);
            }
            t -= ms;
        }
        return new Pose(CompanionFrame.Happy);
    }

    private PixelBuffer FrameOf(Companion c, CompanionFrame frame) =>
        c.IsOrange ? _art.Orange[(int)frame] : _art.Orbs[c.Index % _art.Orbs.Count][(int)frame];

    private double Between((double Min, double Max) range) => range.Min + _random.NextDouble() * (range.Max - range.Min);

    private static int ColorOf(PixelBuffer buffer, int i) => CompanionArt.Read(buffer.Pbgra, i);

    private static PixelBuffer Faded(PixelBuffer source, double alpha)
    {
        var result = new PixelBuffer(source.Width, source.Height, new byte[source.Pbgra.Length]);
        for (var i = 0; i < source.Pbgra.Length; i++)
        {
            result.Pbgra[i] = (byte)Math.Round(source.Pbgra[i] * alpha);
        }
        return result;
    }

    private static PixelBuffer ColorPixel(int color, double alpha)
    {
        var a = Math.Clamp(alpha, 0, 1);
        return new PixelBuffer(1, 1,
        [
            (byte)Math.Round((color & 255) * a),
            (byte)Math.Round(((color >> 8) & 255) * a),
            (byte)Math.Round(((color >> 16) & 255) * a),
            (byte)Math.Round(255 * a),
        ]);
    }

    private readonly record struct Pose(
        CompanionFrame Frame, int Dx = 0, int Dy = 0, double Alpha = 1, bool Pixel = false, bool Visible = true);

    private sealed class Companion(string key, Companion? parent, int index)
    {
        /// <summary>Session id of an orange, agent id of an orb.</summary>
        public string Key { get; } = key;

        /// <summary>The orange an orb belongs to; null for an orange.</summary>
        public Companion? Parent { get; } = parent;

        /// <summary>Orange slot 0..3; an orb uses its orange's slot, which also picks its color variant.</summary>
        public int Index { get; } = index;

        public bool IsOrange => Parent is null;

        public Phase Phase { get; set; } = Phase.Spawning;

        /// <summary>Time in the phase, already multiplied by <see cref="TimeScale"/>.</summary>
        public double PhaseMs { get; set; }

        public double TimeScale { get; set; } = 1;

        public double X { get; set; }

        public double Y { get; set; }

        public PixelPoint Slot { get; set; }

        public double FromX { get; init; }

        public double FromY { get; init; }

        public double BobPhaseMs { get; init; }

        public double BlinkInMs { get; set; }

        public double BlinkMs { get; set; } = -1;

        public double HopInMs { get; set; }

        public double HopMs { get; set; } = -1;

        public List<Sparkle> Sparkles { get; } = [];
    }

    private sealed class Sparkle
    {
        public int X { get; init; }

        public int Y { get; init; }

        public bool On { get; set; }

        public double TimerMs { get; set; }
    }

    private sealed class Particle
    {
        public double X { get; set; }

        public double Y { get; set; }

        public double Vx { get; init; }

        public double Vy { get; init; }

        public double AgeMs { get; set; }

        public int Color { get; init; }
    }
}
