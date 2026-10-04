// Manifest-driven player for the main character. A typed port of the inline player in
// assets/preview/state-demo.html; behavior is meant to be identical. No DOM: time comes from
// updatePet(dtMs), randomness from a seeded RNG.

import { nextRandom, type Rng } from './state';

type Range = [number, number];
type Seq = [string, number][];
type FxSprite = [string, number, number];

interface FxFrame {
  ms: number;
  sprites: FxSprite[];
}

interface StateDef {
  body: string;
  enter?: Seq;
  exit?: Seq;
  eyes: string;
  hairFrameMs?: number;
  durationMs?: number;
  then?: string;
  fx?: {
    anchor: Range;
    loop?: FxFrame[];
    pop?: FxFrame[];
    hold?: { sprite: string; bobPx: number; bobMs: number };
    repeatMs?: number;
  };
  tap?: { frame: string; downMs: number; gapMs: Range; burst: Range; pauseMs: Range };
  paces?: Record<string, { taps: Range; pauseMs: Range }>;
}

export interface Manifest {
  stage: { size: Range; characterOffset: Range };
  hair: { frames: string[]; frameMs: number };
  eyes: { blink: Seq; intervalMs: Range };
  states: Record<string, StateDef>;
  reactions: Record<string, { anchor: Range; frames: FxFrame[] }>;
}

export const STATE_LABELS: Record<string, [string, string]> = {
  idle: ['空闲', 'SessionStart'],
  think: ['思考', 'UserPromptSubmit'],
  working: ['工作中', 'PreToolUse'],
  notice: ['等待确认', 'Notification'],
  done: ['完成', 'Stop'],
  error: ['错误', 'StopFailure'],
  sleep: ['睡觉', '长时间无活动'],
};

export const PACE_LABELS: Record<string, string> = { active: '执行工具', composing: '两次工具之间' };

/** One typical task, copied from state-demo: `~` switches pace, `!` plays a reaction. */
const PLAN: [string, number][] = [
  ['think', 2600], ['~active', 0], ['working', 2400], ['~composing', 3600], ['~active', 1800], ['~composing', 0],
  ['!toolFailure', 3600], ['~active', 0], ['notice', 4800], ['working', 3600], ['done', 15400], ['idle', 2500],
  ['think', 2000], ['error', 4500], ['sleep', 5000], ['idle', 2500],
];

export interface Pet {
  m: Manifest;
  rng: Rng;
  t: number;
  name: string;
  body: string;
  q: Seq;
  qT: number;
  hairI: number;
  hairT: number;
  eye: string | null;
  eyeQ: Seq;
  eyeT: number;
  nextBlink: number;
  fx: FxSprite[];
  fxMode: 'loop' | 'pop' | null;
  fxI: number;
  fxT: number;
  popQ: FxFrame[];
  nextPop: number;
  bob: number;
  bobT: number;
  tapT: number;
  tapLeft: number;
  doneT: number;
  pace: string;
  reactName: string;
  react: FxSprite[];
  reactQ: FxFrame[];
  reactT: number;
  auto: boolean;
  planI: number;
  planT: number;
}

/** One sprite to draw, in pet-stage pixels. Effects are bottom-aligned at y (manifest rule). */
export interface PetLayer {
  name: string;
  x: number;
  y: number;
  bottom: boolean;
}

export function createPet(m: Manifest, seed: number): Pet {
  const pet: Pet = {
    m, rng: { s: seed }, t: 0, name: 'idle', body: m.states.idle.body, q: [], qT: Infinity,
    hairI: 0, hairT: 800, eye: null, eyeQ: [], eyeT: 0, nextBlink: 1500,
    fx: [], fxMode: null, fxI: 0, fxT: 0, popQ: [], nextPop: 0, bob: 0, bobT: 0, tapT: 0, tapLeft: 0,
    doneT: Infinity, pace: 'active', reactName: '', react: [], reactQ: [], reactT: 0,
    auto: true, planI: 0, planT: 2500,
  };
  return pet;
}

const cur = (p: Pet): StateDef => p.m.states[p.name];
const between = (p: Pet, [a, b]: Range): number => a + nextRandom(p.rng) * (b - a);

function nextQ(p: Pet): void {
  const e = p.q.shift();
  if (!e) {
    p.qT = Infinity;
    return;
  }
  p.body = e[0];
  p.qT = e[1] ? p.t + e[1] : Infinity;
}

export function goState(p: Pet, name: string): void {
  if (name === p.name || !p.m.states[name]) return;
  const old = cur(p);
  p.name = name;
  const s = cur(p);
  const lead: Seq = [...(old.exit ?? []), ...(s.enter ?? [])];
  p.q = [...lead.map((e): [string, number] => [e[0], e[1]]), [s.body, 0]];
  nextQ(p);
  p.fx = [];
  p.fxMode = null;
  p.popQ = [];
  p.doneT = s.durationMs ? p.t + s.durationMs : Infinity;
  const delay = lead.reduce((a, e) => a + e[1], 0);
  if (s.fx) {
    if (s.fx.loop) {
      p.fxMode = 'loop';
      p.fxI = -1;
      p.fxT = p.t + delay;
    } else {
      p.fxMode = 'pop';
      p.nextPop = p.t + delay;
    }
  }
  if (s.tap) {
    p.tapT = p.t + delay + 300;
    p.tapLeft = 0;
  }
  if (s.eyes !== 'blink') {
    p.eyeQ = [];
    p.eye = null;
  }
}

/** Only changes the tap rhythm; no state reset and no exit/enter frames. */
export function setPace(p: Pet, pace: string): void {
  p.pace = pace;
}

export function react(p: Pet, name: string): void {
  const r = p.m.reactions[name];
  if (!r) return;
  p.reactName = name;
  p.reactQ = r.frames.slice();
  p.react = p.reactQ[0].sprites;
  p.reactT = p.t + p.reactQ[0].ms;
}

export function setAuto(p: Pet, on: boolean): void {
  if (on && !p.auto) p.planT = p.t;
  p.auto = on;
}

export function updatePet(p: Pet, dtMs: number): void {
  p.t += dtMs;
  const t = p.t;
  const s = cur(p);
  const m = p.m;

  if (t > p.hairT) {
    p.hairI = (p.hairI + 1) % m.hair.frames.length;
    p.hairT = t + (s.hairFrameMs ?? m.hair.frameMs);
  }
  if (t >= p.qT) nextQ(p);

  if (s.eyes === 'blink') {
    if (!p.eyeQ.length && t > p.nextBlink) {
      p.eyeQ = m.eyes.blink.map((e) => [e[0], e[1]]);
      p.eye = p.eyeQ[0][0];
      p.eyeT = t + p.eyeQ[0][1];
    }
    if (p.eyeQ.length && t >= p.eyeT) {
      p.eyeQ.shift();
      if (p.eyeQ.length) {
        p.eye = p.eyeQ[0][0];
        p.eyeT = t + p.eyeQ[0][1];
      } else {
        p.eye = null;
        p.nextBlink = t + between(p, m.eyes.intervalMs);
      }
    }
  } else {
    p.eye = s.eyes === 'off' ? null : s.eyes;
  }

  const fx = s.fx;
  if (fx && p.fxMode === 'loop' && fx.loop && t >= p.fxT) {
    p.fxI = (p.fxI + 1) % fx.loop.length;
    p.fx = fx.loop[p.fxI].sprites.slice();
    p.fxT = t + fx.loop[p.fxI].ms;
  }
  if (fx && p.fxMode === 'pop' && fx.pop && fx.hold) {
    if (!p.popQ.length && t >= p.nextPop) {
      p.popQ = fx.pop.slice();
      p.fx = p.popQ[0].sprites.slice();
      p.fxT = t + p.popQ[0].ms;
    } else if (p.popQ.length && t >= p.fxT) {
      p.popQ.shift();
      if (p.popQ.length) {
        p.fx = p.popQ[0].sprites.slice();
        p.fxT = t + p.popQ[0].ms;
      } else {
        p.fx = [[fx.hold.sprite, 0, 0]];
        p.bob = 0;
        p.bobT = t + fx.hold.bobMs;
        p.nextPop = t + (fx.repeatMs ?? 0);
      }
    }
    if (!p.popQ.length && p.fx.length && t > p.bobT) {
      p.bob = p.bob ? 0 : fx.hold.bobPx;
      p.bobT = t + fx.hold.bobMs;
    }
  }

  const tap = s.tap;
  if (tap && !p.q.length && p.qT === Infinity && t > p.tapT) {
    const pace = s.paces?.[p.pace];
    const burst = pace ? pace.taps : tap.burst;
    const pauseMs = pace ? pace.pauseMs : tap.pauseMs;
    if (p.body === tap.frame) {
      p.body = s.body;
      p.tapT = t + (p.tapLeft > 0 ? between(p, tap.gapMs) : between(p, pauseMs));
    } else {
      if (p.tapLeft <= 0) p.tapLeft = burst[0] + Math.floor(nextRandom(p.rng) * (burst[1] - burst[0] + 1));
      p.tapLeft--;
      p.body = tap.frame;
      p.tapT = t + tap.downMs;
    }
  }

  if (p.reactQ.length && t >= p.reactT) {
    p.reactQ.shift();
    if (p.reactQ.length) {
      p.react = p.reactQ[0].sprites;
      p.reactT = t + p.reactQ[0].ms;
    } else {
      p.react = [];
    }
  }

  if (t >= p.doneT) {
    p.doneT = Infinity;
    if (s.then) goState(p, s.then);
  }

  if (p.auto && t >= p.planT) {
    const [n, ms] = PLAN[p.planI];
    p.planI = (p.planI + 1) % PLAN.length;
    if (n[0] === '!') react(p, n.slice(1));
    else if (n[0] === '~') setPace(p, n.slice(1));
    else goState(p, n);
    p.planT = t + ms;
  }
}

export function petLayers(p: Pet): PetLayer[] {
  const [ox, oy] = p.m.stage.characterOffset;
  const out: PetLayer[] = [];
  const at = (name: string | null, x: number, y: number, bottom: boolean): void => {
    if (name) out.push({ name, x, y, bottom });
  };
  at(p.m.hair.frames[p.hairI], ox, oy, false);
  at(p.body, ox, oy, false);
  at(p.eye, ox, oy, false);
  const fx = cur(p).fx;
  if (fx) {
    const [ax, ay] = fx.anchor;
    const bob = p.fxMode === 'pop' && !p.popQ.length ? p.bob : 0;
    for (const [n, dx, dy] of p.fx) at(n, ax + dx, ay + dy + bob, true);
  }
  if (p.react.length) {
    const [ax, ay] = p.m.reactions[p.reactName].anchor;
    for (const [n, dx, dy] of p.react) at(n, ax + dx, ay + dy, true);
  }
  return out;
}
