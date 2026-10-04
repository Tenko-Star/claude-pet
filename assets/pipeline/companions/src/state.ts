// Pure companion state. No DOM: everything advances through update(w, dtMs) and the explicit events
// spawnOrange / spawnOrb / complete / error / dismiss / removeOrange, each carrying its timestamp.
// Oranges (main agents) and orbs (their sub-agents) share one state machine; type differences come from
// SPRITES. Kept small so it ports to C#. Positions are pet-stage pixels (see sprites.ts).

import {
  FRAME, LAG, LAYOUT, LIMITS, PARTICLE, PET, SPARKLE, SPRITES, TIMING,
  type FrameIndex, type Kind, type Point, type SpriteConfig,
} from './sprites';

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

export const between = (r: Rng, a: number, b: number): number => a + nextRandom(r) * (b - a);

export type Phase = 'spawning' | 'idle' | 'completing' | 'erroring' | 'error' | 'dissolving';

export interface Sparkle {
  x: number; // offset from the orb center
  y: number;
  on: boolean;
  timerMs: number;
}

export interface Instance {
  id: number;
  kind: Kind;
  /** Owning orange of an orb; null for an orange. */
  parent: number | null;
  /** Orange: its fixed slot 0..3. Orb: its parent's slot, which also picks the color variant. */
  index: number;
  phase: Phase;
  /** Time in the phase, already multiplied by the time scale; negative while a spawn waits out its jitter. */
  phaseMs: number;
  /** Phase speed: above 1 for a coalesced exit. */
  timeScale: number;
  pos: Point;
  slot: Point;
  /** Start of a traveling spawn: the parent's center at spawn time. */
  from: Point;
  bobPhaseMs: number;
  blinkInMs: number;
  blinkMs: number; // -1 when not blinking
  hopInMs: number;
  hopMs: number; // -1 when not hopping
  sparkles: Sparkle[];
  /** Timestamp of the latest applied event; older events for this instance are dropped. */
  eventAtMs: number;
  /** Timestamp of an applied event that is not on screen yet, or null. */
  pendingAtMs: number | null;
  /** How far the latest event trailed when it reached the screen. */
  lagMs: number;
}

export interface Particle {
  x: number;
  y: number;
  vx: number; // px per second
  vy: number;
  ageMs: number;
  kind: Kind;
  look: 'variant' | 'gray';
  variant: number;
  color: number; // index into the type's frame palette, or BRIGHT_COLOR
}

/** Particle color of a still-traveling spawn pixel: the look's brightest body color. */
export const BRIGHT_COLOR = -1;

/** Opaque pixel of a frame, as plain data handed over by the loader. */
export interface FramePixel {
  x: number;
  y: number;
  color: number;
}

export interface World {
  timeMs: number;
  instances: Instance[];
  particles: Particle[];
  nextId: number;
  rng: Rng;
  framePixels: Record<Kind, FramePixel[][]>;
  /** Timestamp of the latest spawn; spawns sharing it get a start jitter. */
  lastSpawnAtMs: number;
  /** Events dropped because a newer one for the same instance was already applied. */
  droppedEvents: number;
  /** Worst lag seen recently, for the debug readout. */
  lagPeakMs: number;
  lagPeakAtMs: number;
}

export interface Pose {
  visual: 'none' | 'pixel' | 'frame';
  frame: FrameIndex;
  palette: 'variant' | 'error';
  dx: number;
  dy: number;
  alpha: number;
}

// ---- small pure functions ----

/** Fixed slot of the orange with this index: left-low, right-low, left-high, right-high, clear of the pet's body. */
export function orangeSlot(index: number): Point {
  const e = SPRITES.orange.extent;
  const x = index % 2 === 0
    ? PET.body.left - LAYOUT.orangeGap - 1 - e.right
    : PET.body.right + LAYOUT.orangeGap + 1 + e.left;
  return { x, y: index < 2 ? PET.waistY : PET.shoulderY };
}

/** Orb slots above the head top: shallow arcs of up to arcPerRow, lowest arc first, ends curving down. */
export function arcSlots(count: number): Point[] {
  const out: Point[] = [];
  const per = LAYOUT.arcPerRow;
  const halfSpan = ((per - 1) / 2) * LAYOUT.arcStepX;
  const baseY = PET.headTop.y - LAYOUT.arcGap - 1 - SPRITES.orb.extent.bottom;
  for (let k = 0; k < count; k++) {
    const row = Math.floor(k / per);
    const n = Math.min(per, count - row * per);
    const dx = ((k % per) - (n - 1) / 2) * LAYOUT.arcStepX;
    const t = dx / halfSpan;
    out.push({ x: Math.round(PET.headTop.x + dx), y: Math.round(baseY - row * LAYOUT.arcRowStep + LAYOUT.arcSag * t * t) });
  }
  return out;
}

export function spawnTotalMs(cfg: SpriteConfig): number {
  return cfg.spawnLeadMs + TIMING.landStretchMs + TIMING.landSquashMs;
}

/** Timed transitions only; the others come from the events. */
export function nextPhase(cfg: SpriteConfig, phase: Phase, phaseMs: number): Phase | 'removed' {
  if (phase === 'spawning' && phaseMs >= spawnTotalMs(cfg)) return 'idle';
  if (phase === 'erroring' && phaseMs >= TIMING.shakeMs) return 'error';
  if (phase === 'completing' && phaseMs >= TIMING.completeMs) return 'dissolving';
  if (phase === 'dissolving' && phaseMs >= TIMING.dissolveMs) return 'removed';
  return phase;
}

/** Still an agent: alive and not on its way out. */
export function isLive(i: Instance): boolean {
  return i.phase === 'spawning' || i.phase === 'idle' || i.phase === 'erroring' || i.phase === 'error';
}

export function holdsSlot(i: Instance): boolean {
  return i.phase !== 'dissolving';
}

export function sparklesVisible(phase: Phase): boolean {
  return phase === 'idle' || phase === 'completing';
}

/** Speed of a coalesced exit, so a phase of totalMs fits in LAG.coalescedExitMs. */
export function coalescedScale(totalMs: number): number {
  return Math.min(LAG.maxTimeScale, Math.max(1, totalMs / LAG.coalescedExitMs));
}

/** Speed-up for an instance whose latest event is still not on screen after lagMs. */
export function catchUpScale(lagMs: number): number {
  if (lagMs <= LAG.catchUpFromMs) return 1;
  const t = Math.min(1, (lagMs - LAG.catchUpFromMs) / (LAG.budgetMs - LAG.catchUpFromMs));
  return 1 + t * (LAG.maxTimeScale - 1);
}

export function bobOffsetY(timeMs: number, bobPhaseMs: number): number {
  return Math.floor((timeMs + bobPhaseMs) / TIMING.bobStepMs) % 2 === 0 ? 0 : -1;
}

/** Idle hop: stretch while 1 px up, base still up, squash on landing. */
export function hopPose(hopMs: number): { frame: FrameIndex; dy: number } | null {
  if (hopMs < 0) return null;
  if (hopMs < TIMING.hopStretchMs) return { frame: FRAME.stretch, dy: -1 };
  if (hopMs < TIMING.hopStretchMs + TIMING.hopTopMs) return { frame: FRAME.base, dy: -1 };
  if (hopMs < hopTotalMs()) return { frame: FRAME.squash, dy: 0 };
  return null;
}

export function hopTotalMs(): number {
  return TIMING.hopStretchMs + TIMING.hopTopMs + TIMING.hopSquashMs;
}

export function blinkTotalMs(cfg: SpriteConfig): number {
  return cfg.blink.reduce((sum, s) => sum + s.ms, 0);
}

/** Frame of the blink sequence at blinkMs, or null once it is over. */
export function blinkFrame(cfg: SpriteConfig, blinkMs: number): FrameIndex | null {
  if (blinkMs < 0) return null;
  let t = blinkMs;
  for (const step of cfg.blink) {
    if (t < step.ms) return step.frame;
    t -= step.ms;
  }
  return null;
}

/** Completion hop keeps the happy face in the air: up 1, up 2, up 1, then squash on landing. */
export function completePose(phaseMs: number): { frame: FrameIndex; dy: number } {
  const [up1, up2, down1, land] = TIMING.completeHopMs;
  const t = phaseMs - TIMING.completeHopAtMs;
  if (t < 0) return { frame: FRAME.happy, dy: 0 };
  if (t < up1) return { frame: FRAME.happy, dy: -1 };
  if (t < up1 + up2) return { frame: FRAME.happy, dy: -2 };
  if (t < up1 + up2 + down1) return { frame: FRAME.happy, dy: -1 };
  if (t < up1 + up2 + down1 + land) return { frame: FRAME.squash, dy: 0 };
  return { frame: FRAME.happy, dy: 0 };
}

export function pose(i: Instance, timeMs: number): Pose {
  const cfg = SPRITES[i.kind];
  const frame = (f: FrameIndex, dx = 0, dy = 0, palette: Pose['palette'] = 'variant', alpha = 1): Pose => ({
    visual: 'frame', frame: f, palette, dx, dy, alpha,
  });
  switch (i.phase) {
    case 'spawning': {
      if (i.phaseMs < 0) return { visual: 'none', frame: FRAME.base, palette: 'variant', dx: 0, dy: 0, alpha: 0 };
      if (i.phaseMs < cfg.spawnLeadMs) {
        if (cfg.spawn === 'travel') return { visual: 'pixel', frame: FRAME.base, palette: 'variant', dx: 0, dy: 0, alpha: 1 };
        return frame(FRAME.base, 0, 0, 'variant', i.phaseMs / cfg.spawnLeadMs);
      }
      const landMs = i.phaseMs - cfg.spawnLeadMs;
      return frame(landMs < TIMING.landStretchMs ? FRAME.stretch : FRAME.squash);
    }
    case 'idle': {
      const hop = hopPose(i.hopMs);
      if (hop) return frame(hop.frame, 0, hop.dy);
      return frame(blinkFrame(cfg, i.blinkMs) ?? FRAME.base, 0, bobOffsetY(timeMs, i.bobPhaseMs));
    }
    case 'completing': {
      const p = completePose(i.phaseMs);
      return frame(p.frame, 0, p.dy);
    }
    case 'erroring':
      return frame(FRAME.error, Math.floor(i.phaseMs / TIMING.shakeStepMs) % 2 === 0 ? 1 : -1, 0, 'error');
    case 'error':
      return frame(FRAME.error, 0, 0, 'error');
    case 'dissolving':
      return { visual: 'none', frame: FRAME.base, palette: 'variant', dx: 0, dy: 0, alpha: 0 };
  }
}

const easeOutQuad = (t: number): number => 1 - (1 - t) * (1 - t);

// ---- world queries ----

export function createWorld(framePixels: Record<Kind, FramePixel[][]>, seed: number): World {
  return {
    timeMs: 0, instances: [], particles: [], nextId: 1, rng: { s: seed }, framePixels,
    lastSpawnAtMs: -1, droppedEvents: 0, lagPeakMs: 0, lagPeakAtMs: 0,
  };
}

export function find(w: World, id: number): Instance | undefined {
  return w.instances.find((i) => i.id === id);
}

export function oranges(w: World): Instance[] {
  return w.instances.filter((i) => i.kind === 'orange');
}

export function children(w: World, orangeId: number): Instance[] {
  return w.instances.filter((i) => i.parent === orangeId);
}

/** Orange holding slot `index`, if any. */
export function orangeAt(w: World, index: number): Instance | undefined {
  return w.instances.find((i) => i.kind === 'orange' && i.index === index && holdsSlot(i));
}

/** Lowest free orange slot, or -1 when all are taken. */
export function freeOrangeIndex(w: World): number {
  for (let k = 0; k < LIMITS.oranges; k++) if (!orangeAt(w, k)) return k;
  return -1;
}

export function canSpawnOrb(w: World, orangeId: number): boolean {
  const parent = find(w, orangeId);
  return !!parent && parent.kind === 'orange' && isLive(parent)
    && children(w, orangeId).filter(holdsSlot).length < LIMITS.orbsPerOrange;
}

/** Largest lag of an event that is not on screen yet. */
export function currentLagMs(w: World): number {
  let lag = 0;
  for (const i of w.instances) if (i.pendingAtMs !== null) lag = Math.max(lag, w.timeMs - i.pendingAtMs);
  return lag;
}

// ---- events ----

/** Applies an event's timestamp, or drops it if a newer event for the instance came first. */
function accept(w: World, i: Instance, atMs: number): boolean {
  if (atMs < i.eventAtMs) {
    w.droppedEvents++;
    return false;
  }
  i.eventAtMs = atMs;
  return true;
}

/** The response to the event at atMs is on screen from now on. */
function shown(w: World, i: Instance, atMs: number): void {
  i.pendingAtMs = null;
  i.lagMs = Math.max(0, w.timeMs - atMs);
  if (i.lagMs >= w.lagPeakMs) {
    w.lagPeakMs = i.lagMs;
    w.lagPeakAtMs = w.timeMs;
  }
}

function create(w: World, kind: Kind, parent: Instance | null, index: number, atMs: number): Instance {
  const r = w.rng;
  const cfg = SPRITES[kind];
  const sparkles: Sparkle[] = [];
  if (cfg.sparkles) {
    const n = SPARKLE.count[0] + Math.floor(nextRandom(r) * (SPARKLE.count[1] - SPARKLE.count[0] + 1));
    for (let k = 0; k < n; k++) {
      const rad = ((k + between(r, 0.15, 0.85)) / n) * Math.PI * 2;
      const dist = SPARKLE.distance[0] + Math.floor(nextRandom(r) * (SPARKLE.distance[1] - SPARKLE.distance[0] + 1));
      sparkles.push({
        x: Math.round(Math.cos(rad) * dist), y: Math.round(Math.sin(rad) * dist),
        on: nextRandom(r) < 0.5, timerMs: between(r, TIMING.sparkleMs[0], TIMING.sparkleMs[1]),
      });
    }
  }
  // Spawns sharing a timestamp run in parallel, a few ms apart; a spawn is timed from its event, not from now.
  const jitter = atMs === w.lastSpawnAtMs ? Math.floor(nextRandom(r) * (LAG.spawnJitterMs + 1)) : 0;
  w.lastSpawnAtMs = atMs;
  const start = parent ? { ...parent.pos } : orangeSlot(index);
  const i: Instance = {
    id: w.nextId++, kind, parent: parent ? parent.id : null, index,
    phase: 'spawning', phaseMs: w.timeMs - atMs - jitter, timeScale: 1,
    pos: { ...start }, slot: { ...start }, from: { ...start },
    bobPhaseMs: Math.floor(nextRandom(r) * TIMING.bobStepMs * 2),
    blinkInMs: between(r, cfg.blinkEveryMs[0], cfg.blinkEveryMs[1]), blinkMs: -1,
    hopInMs: between(r, TIMING.hopEveryMs[0], TIMING.hopEveryMs[1]), hopMs: -1,
    sparkles, eventAtMs: atMs, pendingAtMs: atMs, lagMs: 0,
  };
  w.instances.push(i);
  if (i.phaseMs >= 0) shown(w, i, atMs);
  assignSlots(w);
  return i;
}

/** A main agent started. Returns its id, or null when all orange slots are taken. */
export function spawnOrange(w: World, atMs: number): number | null {
  const index = freeOrangeIndex(w);
  return index < 0 ? null : create(w, 'orange', null, index, atMs).id;
}

/** A sub-agent of an orange started. Returns its id, or null when the orange is gone or full. */
export function spawnOrb(w: World, orangeId: number, atMs: number): number | null {
  if (!canSpawnOrb(w, orangeId)) return null;
  const parent = find(w, orangeId);
  return parent ? create(w, 'orb', parent, parent.index, atMs).id : null;
}

function enter(i: Instance, phase: Phase, timeScale = 1): void {
  i.phase = phase;
  i.phaseMs = 0;
  i.timeScale = timeScale;
  i.blinkMs = -1;
  i.hopMs = -1;
}

/** Happy face and a hop, then dissolve. While spawning: skip to the slot and play it compressed. */
function beginComplete(w: World, i: Instance, atMs: number, timeScale: number): boolean {
  if (i.phase === 'spawning') i.pos = { ...i.slot };
  else if (i.phase !== 'idle') return false;
  enter(i, 'completing', timeScale);
  shown(w, i, atMs);
  return true;
}

/** An agent finished. An orange takes its orbs along, so they all dissolve together. */
export function complete(w: World, id: number, atMs: number): boolean {
  const i = find(w, id);
  if (!i || !accept(w, i, atMs)) return false;
  const scale = i.phase === 'spawning' ? coalescedScale(TIMING.completeMs) : 1;
  if (!beginComplete(w, i, atMs, scale)) return false;
  if (i.kind === 'orange') {
    for (const c of children(w, i.id)) {
      c.eventAtMs = Math.max(c.eventAtMs, atMs);
      beginComplete(w, c, atMs, scale);
    }
  }
  return true;
}

/** An agent failed: error face and a shake, then it stays in error until dismissed. */
export function error(w: World, id: number, atMs: number): boolean {
  const i = find(w, id);
  if (!i || !accept(w, i, atMs)) return false;
  if (i.phase === 'spawning') {
    i.pos = { ...i.slot };
    enter(i, 'erroring', coalescedScale(TIMING.shakeMs));
  } else if (i.phase === 'idle') {
    enter(i, 'erroring');
  } else {
    return false;
  }
  shown(w, i, atMs);
  return true;
}

/** Clears an errored instance; it dissolves with gray particles (an orange with all its orbs). */
export function dismiss(w: World, id: number, atMs: number): boolean {
  const i = find(w, id);
  if (!i || !accept(w, i, atMs) || (i.phase !== 'error' && i.phase !== 'erroring')) return false;
  dissolveWithOrbs(w, i);
  shown(w, i, atMs);
  return true;
}

/** A main agent went away: the orange and all its orbs dissolve at once. */
export function removeOrange(w: World, id: number, atMs: number): boolean {
  const i = find(w, id);
  if (!i || i.kind !== 'orange' || !accept(w, i, atMs) || i.phase === 'dissolving') return false;
  dissolveWithOrbs(w, i);
  shown(w, i, atMs);
  return true;
}

// ---- internals ----

/** Orbs holding a slot fill the arcs, grouped by orange, then by age. Orange slots are fixed. */
function assignSlots(w: World): void {
  const orbs = w.instances
    .filter((i) => i.kind === 'orb' && holdsSlot(i))
    .sort((a, b) => a.index - b.index || a.id - b.id);
  const arc = arcSlots(orbs.length);
  orbs.forEach((i, n) => (i.slot = arc[n]));
  for (const o of oranges(w)) o.slot = orangeSlot(o.index);
}

function dissolveWithOrbs(w: World, i: Instance): void {
  startDissolve(w, i);
  if (i.kind !== 'orange') return;
  for (const c of children(w, i.id)) {
    if (c.phase === 'dissolving') continue;
    startDissolve(w, c);
    c.pendingAtMs = null;
  }
}

function startDissolve(w: World, i: Instance): void {
  const cfg = SPRITES[i.kind];
  const p = pose(i, w.timeMs);
  const look: Particle['look'] = i.phase === 'error' || i.phase === 'erroring' ? 'gray' : 'variant';
  const ax = Math.floor(i.pos.x);
  const ay = Math.floor(i.pos.y);
  const r = w.rng;
  const emit = (x: number, y: number, rx: number, ry: number, color: number): void => {
    const len = Math.hypot(rx, ry) || 1;
    const out = between(r, PARTICLE.outward[0], PARTICLE.outward[1]);
    w.particles.push({
      x, y,
      vx: (rx / len) * out + between(r, -PARTICLE.jitter, PARTICLE.jitter),
      vy: (ry / len) * out * 0.5 - between(r, PARTICLE.upward[0], PARTICLE.upward[1]),
      ageMs: 0, kind: i.kind, look, variant: i.index, color,
    });
  };
  if (p.visual === 'pixel') {
    emit(ax, ay, 0, -1, BRIGHT_COLOR);
  } else if (i.phase !== 'dissolving') {
    // A spawn still waiting out its jitter dissolves from its base frame, so it is never dropped unseen.
    const frame = p.visual === 'frame' ? p.frame : FRAME.base;
    const ox = ax + p.dx - cfg.center.x;
    const oy = ay + p.dy - cfg.center.y;
    for (const px of w.framePixels[i.kind][frame] ?? []) {
      emit(ox + px.x, oy + px.y, px.x - cfg.center.x, px.y - cfg.center.y, px.color);
    }
  }
  enter(i, 'dissolving');
  assignSlots(w);
}

function tickIdle(i: Instance, cfg: SpriteConfig, r: Rng, dtMs: number): void {
  if (i.hopMs >= 0) {
    i.hopMs += dtMs;
    if (i.hopMs >= hopTotalMs()) {
      i.hopMs = -1;
      i.hopInMs = between(r, TIMING.hopEveryMs[0], TIMING.hopEveryMs[1]);
    }
  } else if ((i.hopInMs -= dtMs) <= 0 && i.blinkMs < 0) {
    i.hopMs = 0;
  }
  if (i.blinkMs >= 0) {
    i.blinkMs += dtMs;
    if (i.blinkMs >= blinkTotalMs(cfg)) {
      i.blinkMs = -1;
      i.blinkInMs = between(r, cfg.blinkEveryMs[0], cfg.blinkEveryMs[1]);
    }
  } else if (i.hopMs < 0 && (i.blinkInMs -= dtMs) <= 0) {
    i.blinkMs = 0;
  }
}

export function update(w: World, dtMs: number): void {
  w.timeMs += dtMs;
  const r = w.rng;

  const glide = 1 - Math.exp(-dtMs / TIMING.glideMs);
  for (const i of w.instances) {
    const cfg = SPRITES[i.kind];
    const scale = i.pendingAtMs === null ? i.timeScale : Math.max(i.timeScale, catchUpScale(w.timeMs - i.pendingAtMs));
    i.phaseMs += dtMs * scale;
    if (i.pendingAtMs !== null && i.phase === 'spawning' && i.phaseMs >= 0) shown(w, i, i.pendingAtMs);

    for (const s of i.sparkles) {
      s.timerMs -= dtMs;
      if (s.timerMs <= 0) {
        s.on = !s.on;
        s.timerMs = between(r, TIMING.sparkleMs[0], TIMING.sparkleMs[1]);
      }
    }

    if (i.phase === 'idle') tickIdle(i, cfg, r, dtMs);

    if (i.phase === 'spawning' && cfg.spawn === 'travel' && i.phaseMs < cfg.spawnLeadMs) {
      const k = easeOutQuad(Math.max(0, i.phaseMs) / cfg.spawnLeadMs);
      i.pos = { x: i.from.x + (i.slot.x - i.from.x) * k, y: i.from.y + (i.slot.y - i.from.y) * k };
    } else if (i.phase !== 'dissolving') {
      i.pos = { x: i.pos.x + (i.slot.x - i.pos.x) * glide, y: i.pos.y + (i.slot.y - i.pos.y) * glide };
    }
  }

  for (const i of w.instances.slice()) {
    const next = nextPhase(SPRITES[i.kind], i.phase, i.phaseMs);
    if (next === i.phase || next === 'removed') continue;
    if (next === 'dissolving') dissolveWithOrbs(w, i);
    else enter(i, next);
  }
  w.instances = w.instances.filter((i) => nextPhase(SPRITES[i.kind], i.phase, i.phaseMs) !== 'removed');

  for (const p of w.particles) {
    p.ageMs += dtMs;
    p.x += (p.vx * dtMs) / 1000;
    p.y += (p.vy * dtMs) / 1000;
  }
  w.particles = w.particles.filter((p) => p.ageMs < TIMING.dissolveMs);

  if (w.timeMs - w.lagPeakAtMs > LAG.peakHoldMs) {
    w.lagPeakMs = currentLagMs(w);
    w.lagPeakAtMs = w.timeMs;
  }
}

/** Topmost visible instance under a pet-stage point, orbs before oranges, or null. */
export function hitTest(w: World, pt: Point): Instance | null {
  const order = [
    ...w.instances.filter((i) => i.kind === 'orb').reverse(),
    ...w.instances.filter((i) => i.kind === 'orange').reverse(),
  ];
  for (const i of order) {
    if (pose(i, w.timeMs).visual !== 'frame') continue;
    const e = SPRITES[i.kind].extent;
    const dx = pt.x - Math.floor(i.pos.x);
    const dy = pt.y - Math.floor(i.pos.y);
    if (dx >= -e.left && dx <= e.right && dy >= -e.top && dy <= e.bottom) return i;
  }
  return null;
}
