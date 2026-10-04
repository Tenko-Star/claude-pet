// Pure orb-companion state. No DOM: everything advances through update(w, dtMs) and the explicit
// events spawn / complete / fail / dismiss. Units are sprite pixels relative to the main
// character's body center (the anchor); times are milliseconds. Kept small so it ports to C#.

export interface Point {
  x: number;
  y: number;
}

export interface Rng {
  s: number;
}

/** mulberry32; state lives in the Rng object so the whole world stays plain data. */
export function nextRandom(r: Rng): number {
  r.s = (r.s + 0x6d2b79f5) | 0;
  let t = r.s;
  t = Math.imul(t ^ (t >>> 15), t | 1);
  t ^= t + Math.imul(t ^ (t >>> 7), t | 61);
  return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
}

const between = (r: Rng, a: number, b: number): number => a + nextRandom(r) * (b - a);

// Frame indices, matching assets/subagent/orb/orb_0N_*.png.
export const FRAME = { base: 0, squash: 1, stretch: 2, happy: 3, error: 4, blink: 5 } as const;
export type FrameIndex = (typeof FRAME)[keyof typeof FRAME];
export const FRAME_SIZE = 24;
/** Body center inside a 24x24 frame; the companion position refers to this pixel. */
export const FRAME_CENTER: Point = { x: 12, y: 12 };
const BODY_RADIUS = 7;

export const MAX_COUNT = 8;
export const VARIANT_COUNT = 4; // original, blue, green, purple

export const TIMING = {
  travelMs: 400,
  landStretchMs: 80,
  landSquashMs: 80,
  bobStepMs: 500,
  blinkMs: 120,
  blinkEveryMs: [3000, 6000],
  hopEveryMs: [3000, 5000],
  hopStretchMs: 100,
  hopTopMs: 100,
  hopSquashMs: 80,
  completeMs: 700,
  completeHopAtMs: 120,
  shakeMs: 300,
  shakeStepMs: 60,
  dissolveMs: 600,
  sparkleMs: [200, 900],
  churnMs: [800, 2500],
  churnDismissAfterMs: 4000,
  glideMs: 120,
} as const;

export const ARC = { radius: 80, stepDeg: 30, maxSpanDeg: 180 } as const;

export type Phase = 'spawning' | 'idle' | 'completing' | 'erroring' | 'error' | 'dissolving';

export interface Sparkle {
  x: number; // offset from the body center
  y: number;
  on: boolean;
  timerMs: number;
}

export interface Companion {
  id: number;
  variant: number;
  phase: Phase;
  phaseMs: number;
  pos: Point;
  slot: Point;
  bobPhaseMs: number;
  blinkInMs: number;
  blinkMs: number; // -1 when not blinking
  hopInMs: number;
  hopMs: number; // -1 when not hopping
  sparkles: Sparkle[];
  gray: boolean; // dissolves with gray particles (came from error)
}

export interface Particle {
  x: number;
  y: number;
  vx: number; // px per second
  vy: number;
  ageMs: number;
  palette: 'variant' | 'gray';
  variant: number;
  color: number; // index into the frame palette
}

/** Opaque pixel of a frame, as plain data handed over by the loader. */
export interface FramePixel {
  x: number;
  y: number;
  color: number;
}

export interface World {
  timeMs: number;
  companions: Companion[];
  particles: Particle[];
  nextId: number;
  nextVariant: number;
  churn: boolean;
  churnTimerMs: number;
  rng: Rng;
  framePixels: FramePixel[][];
}

export interface Pose {
  visual: 'none' | 'pixel' | 'frame';
  frame: FrameIndex;
  palette: 'variant' | 'error';
  dx: number;
  dy: number;
}

// ---- small pure functions ----

/** Slots on an arc above the anchor; the span grows with the count and stays centered on top. */
export function arcSlots(count: number): Point[] {
  if (count <= 0) return [];
  const span = Math.min(ARC.maxSpanDeg, ARC.stepDeg * (count - 1));
  const out: Point[] = [];
  for (let i = 0; i < count; i++) {
    const deg = count === 1 ? 90 : 90 + span / 2 - (span * i) / (count - 1);
    const rad = (deg * Math.PI) / 180;
    out.push({ x: Math.round(ARC.radius * Math.cos(rad)), y: Math.round(-ARC.radius * Math.sin(rad)) });
  }
  return out;
}

export function spawnTotalMs(): number {
  return TIMING.travelMs + TIMING.landStretchMs + TIMING.landSquashMs;
}

/** Timed transitions only; event-driven ones live in complete / fail / dismiss. */
export function nextPhase(phase: Phase, phaseMs: number): Phase | 'removed' {
  if (phase === 'spawning' && phaseMs >= spawnTotalMs()) return 'idle';
  if (phase === 'completing' && phaseMs >= TIMING.completeMs) return 'dissolving';
  if (phase === 'erroring' && phaseMs >= TIMING.shakeMs) return 'error';
  if (phase === 'dissolving' && phaseMs >= TIMING.dissolveMs) return 'removed';
  return phase;
}

export function isLive(phase: Phase): boolean {
  return phase === 'spawning' || phase === 'idle' || phase === 'erroring' || phase === 'error';
}

export function sparklesVisible(phase: Phase): boolean {
  return phase === 'idle' || phase === 'completing';
}

export function bobOffsetY(timeMs: number, bobPhaseMs: number): number {
  return Math.floor((timeMs + bobPhaseMs) / TIMING.bobStepMs) % 2 === 0 ? 0 : -1;
}

/** Idle hop: stretch while 1 px up, base still up, squash on landing. */
export function hopPose(hopMs: number): { frame: FrameIndex; dy: number } | null {
  if (hopMs < 0) return null;
  if (hopMs < TIMING.hopStretchMs) return { frame: FRAME.stretch, dy: -1 };
  if (hopMs < TIMING.hopStretchMs + TIMING.hopTopMs) return { frame: FRAME.base, dy: -1 };
  if (hopMs < TIMING.hopStretchMs + TIMING.hopTopMs + TIMING.hopSquashMs) return { frame: FRAME.squash, dy: 0 };
  return null;
}

export function hopTotalMs(): number {
  return TIMING.hopStretchMs + TIMING.hopTopMs + TIMING.hopSquashMs;
}

/** Completion hop keeps the happy face in the air: up 1, up 2, up 1, then squash on landing. */
export function completePose(phaseMs: number): { frame: FrameIndex; dy: number } {
  const t = phaseMs - TIMING.completeHopAtMs;
  if (t < 0) return { frame: FRAME.happy, dy: 0 };
  if (t < 80) return { frame: FRAME.happy, dy: -1 };
  if (t < 180) return { frame: FRAME.happy, dy: -2 };
  if (t < 260) return { frame: FRAME.happy, dy: -1 };
  if (t < 340) return { frame: FRAME.squash, dy: 0 };
  return { frame: FRAME.happy, dy: 0 };
}

export function orbPose(c: Companion, timeMs: number): Pose {
  const pose = (frame: FrameIndex, dx = 0, dy = 0, palette: Pose['palette'] = 'variant'): Pose => ({
    visual: 'frame', frame, palette, dx, dy,
  });
  switch (c.phase) {
    case 'spawning': {
      if (c.phaseMs < TIMING.travelMs) return { visual: 'pixel', frame: FRAME.base, palette: 'variant', dx: 0, dy: 0 };
      const landMs = c.phaseMs - TIMING.travelMs;
      return pose(landMs < TIMING.landStretchMs ? FRAME.stretch : FRAME.squash);
    }
    case 'idle': {
      const hop = hopPose(c.hopMs);
      if (hop) return pose(hop.frame, 0, hop.dy);
      return pose(c.blinkMs >= 0 ? FRAME.blink : FRAME.base, 0, bobOffsetY(timeMs, c.bobPhaseMs));
    }
    case 'completing': {
      const p = completePose(c.phaseMs);
      return pose(p.frame, 0, p.dy);
    }
    case 'erroring':
      return pose(FRAME.error, Math.floor(c.phaseMs / TIMING.shakeStepMs) % 2 === 0 ? 1 : -1, 0, 'error');
    case 'error':
      return pose(FRAME.error, 0, 0, 'error');
    case 'dissolving':
      return { visual: 'none', frame: FRAME.base, palette: 'variant', dx: 0, dy: 0 };
  }
}

const easeOutQuad = (t: number): number => 1 - (1 - t) * (1 - t);

// ---- world ----

export function createWorld(framePixels: FramePixel[][], seed: number): World {
  return {
    timeMs: 0, companions: [], particles: [], nextId: 1, nextVariant: 0,
    churn: false, churnTimerMs: 0, rng: { s: seed }, framePixels,
  };
}

export function liveCount(w: World): number {
  return w.companions.filter((c) => isLive(c.phase)).length;
}

export function spawn(w: World): void {
  if (liveCount(w) >= MAX_COUNT) return;
  const r = w.rng;
  const n = 3 + Math.floor(nextRandom(r) * 3);
  const sparkles: Sparkle[] = [];
  for (let i = 0; i < n; i++) {
    const rad = ((i + between(r, 0.15, 0.85)) / n) * Math.PI * 2;
    const dist = BODY_RADIUS + 2 + Math.floor(nextRandom(r) * 2);
    sparkles.push({
      x: Math.round(Math.cos(rad) * dist), y: Math.round(Math.sin(rad) * dist),
      on: nextRandom(r) < 0.5, timerMs: between(r, TIMING.sparkleMs[0], TIMING.sparkleMs[1]),
    });
  }
  w.companions.push({
    id: w.nextId++, variant: w.nextVariant, phase: 'spawning', phaseMs: 0,
    pos: { x: 0, y: 0 }, slot: { x: 0, y: 0 },
    bobPhaseMs: Math.floor(nextRandom(r) * TIMING.bobStepMs * 2),
    blinkInMs: between(r, TIMING.blinkEveryMs[0], TIMING.blinkEveryMs[1]), blinkMs: -1,
    hopInMs: between(r, TIMING.hopEveryMs[0], TIMING.hopEveryMs[1]), hopMs: -1,
    sparkles, gray: false,
  });
  w.nextVariant = (w.nextVariant + 1) % VARIANT_COUNT;
  assignSlots(w);
}

function enter(c: Companion, phase: Phase): void {
  c.phase = phase;
  c.phaseMs = 0;
  c.blinkMs = -1;
  c.hopMs = -1;
}

export function complete(w: World, id: number): void {
  const c = w.companions.find((x) => x.id === id);
  if (c && c.phase === 'idle') enter(c, 'completing');
}

export function fail(w: World, id: number): void {
  const c = w.companions.find((x) => x.id === id);
  if (c && c.phase === 'idle') enter(c, 'erroring');
}

export function dismiss(w: World, id: number): void {
  const c = w.companions.find((x) => x.id === id);
  if (!c || (c.phase !== 'error' && c.phase !== 'erroring')) return;
  c.gray = true;
  startDissolve(w, c);
}

/** + spawns; − completes the newest idle companion, or dismisses the newest errored one. */
export function setCount(w: World, n: number): void {
  const target = Math.max(0, Math.min(MAX_COUNT, n));
  while (liveCount(w) < target) spawn(w);
  let excess = liveCount(w) - target;
  for (let i = w.companions.length - 1; i >= 0 && excess > 0; i--) {
    if (w.companions[i].phase === 'idle') {
      complete(w, w.companions[i].id);
      excess--;
    }
  }
  for (let i = w.companions.length - 1; i >= 0 && excess > 0; i--) {
    const c = w.companions[i];
    if (c.phase === 'error' || c.phase === 'erroring') {
      dismiss(w, c.id);
      excess--;
    }
  }
}

export function setChurn(w: World, on: boolean): void {
  w.churn = on;
  w.churnTimerMs = 0;
}

/** Companions that still hold a slot (everything but dissolving), ordered by id. */
function assignSlots(w: World): void {
  const holders = w.companions.filter((c) => c.phase !== 'dissolving');
  const slots = arcSlots(holders.length);
  holders.forEach((c, i) => (c.slot = slots[i]));
}

function startDissolve(w: World, c: Companion): void {
  const pose = orbPose(c, w.timeMs);
  const pixels = w.framePixels[pose.frame] ?? [];
  const r = w.rng;
  const ox = Math.floor(c.pos.x) + pose.dx - FRAME_CENTER.x;
  const oy = Math.floor(c.pos.y) + pose.dy - FRAME_CENTER.y;
  for (const p of pixels) {
    const rx = p.x - FRAME_CENTER.x;
    const ry = p.y - FRAME_CENTER.y;
    const len = Math.hypot(rx, ry) || 1;
    const out = between(r, 6, 14);
    w.particles.push({
      x: ox + p.x, y: oy + p.y,
      vx: (rx / len) * out + between(r, -4, 4),
      vy: (ry / len) * out * 0.5 - between(r, 14, 26),
      ageMs: 0, palette: c.gray ? 'gray' : 'variant', variant: c.variant, color: p.color,
    });
  }
  enter(c, 'dissolving');
  assignSlots(w);
}

function churnStep(w: World): void {
  const r = w.rng;
  const stale = w.companions.find((c) => c.phase === 'error' && c.phaseMs >= TIMING.churnDismissAfterMs);
  if (stale) {
    dismiss(w, stale.id);
    return;
  }
  const idle = w.companions.filter((c) => c.phase === 'idle');
  if (liveCount(w) < MAX_COUNT && (idle.length === 0 || nextRandom(r) < 0.5)) {
    spawn(w);
    return;
  }
  if (idle.length === 0) return;
  const c = idle[Math.floor(nextRandom(r) * idle.length)];
  if (nextRandom(r) < 0.8) complete(w, c.id);
  else fail(w, c.id);
}

export function update(w: World, dtMs: number): void {
  w.timeMs += dtMs;
  const r = w.rng;

  if (w.churn) {
    w.churnTimerMs -= dtMs;
    if (w.churnTimerMs <= 0) {
      churnStep(w);
      w.churnTimerMs = between(r, TIMING.churnMs[0], TIMING.churnMs[1]);
    }
  }

  const glide = 1 - Math.exp(-dtMs / TIMING.glideMs);
  for (const c of w.companions) {
    c.phaseMs += dtMs;

    for (const s of c.sparkles) {
      s.timerMs -= dtMs;
      if (s.timerMs <= 0) {
        s.on = !s.on;
        s.timerMs = between(r, TIMING.sparkleMs[0], TIMING.sparkleMs[1]);
      }
    }

    if (c.phase === 'idle') {
      if (c.hopMs >= 0) {
        c.hopMs += dtMs;
        if (c.hopMs >= hopTotalMs()) {
          c.hopMs = -1;
          c.hopInMs = between(r, TIMING.hopEveryMs[0], TIMING.hopEveryMs[1]);
        }
      } else if ((c.hopInMs -= dtMs) <= 0 && c.blinkMs < 0) {
        c.hopMs = 0;
      }
      if (c.blinkMs >= 0) {
        c.blinkMs += dtMs;
        if (c.blinkMs >= TIMING.blinkMs) {
          c.blinkMs = -1;
          c.blinkInMs = between(r, TIMING.blinkEveryMs[0], TIMING.blinkEveryMs[1]);
        }
      } else if (c.hopMs < 0 && (c.blinkInMs -= dtMs) <= 0) {
        c.blinkMs = 0;
      }
    }

    if (c.phase === 'spawning' && c.phaseMs < TIMING.travelMs) {
      const k = easeOutQuad(c.phaseMs / TIMING.travelMs);
      c.pos = { x: c.slot.x * k, y: c.slot.y * k };
    } else if (c.phase !== 'dissolving') {
      c.pos = { x: c.pos.x + (c.slot.x - c.pos.x) * glide, y: c.pos.y + (c.slot.y - c.pos.y) * glide };
    }
  }

  for (const c of w.companions.slice()) {
    const next = nextPhase(c.phase, c.phaseMs);
    if (next === c.phase) continue;
    if (next === 'dissolving') startDissolve(w, c);
    else if (next !== 'removed') enter(c, next);
  }
  if (w.companions.some((c) => nextPhase(c.phase, c.phaseMs) === 'removed')) {
    w.companions = w.companions.filter((c) => nextPhase(c.phase, c.phaseMs) !== 'removed');
  }

  for (const p of w.particles) {
    p.ageMs += dtMs;
    p.x += (p.vx * dtMs) / 1000;
    p.y += (p.vy * dtMs) / 1000;
  }
  w.particles = w.particles.filter((p) => p.ageMs < TIMING.dissolveMs);
}

/** Topmost live companion whose 14x14 body box contains p, or null. */
export function hitTest(w: World, p: Point): number | null {
  for (let i = w.companions.length - 1; i >= 0; i--) {
    const c = w.companions[i];
    if (c.phase === 'dissolving' || (c.phase === 'spawning' && c.phaseMs < TIMING.travelMs)) continue;
    const dx = p.x - Math.floor(c.pos.x);
    const dy = p.y - Math.floor(c.pos.y);
    if (dx >= -BODY_RADIUS && dx < BODY_RADIUS && dy >= -BODY_RADIUS && dy < BODY_RADIUS) return c.id;
  }
  return null;
}
