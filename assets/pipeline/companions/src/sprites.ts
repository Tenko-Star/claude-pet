// Per-type sprite configuration, the pet's layout config and every timing and layout constant, in one place.
// No DOM: plain data that ports to C# records. Units are sprite pixels and milliseconds.
// Positions are in pet-stage pixels: the manifest stage (119x153) of the pet, top-left at (0, 0).

export interface Point {
  x: number;
  y: number;
}

/** Pixel distances from a body center to the outermost opaque pixel over all frames. */
export interface Extent {
  left: number;
  right: number;
  top: number;
  bottom: number;
}

/** orange: a main agent. orb: a sub-agent; it belongs to one orange. */
export type Kind = 'orange' | 'orb';

/** Frame indices shared by both types (file order orange_0N / orb_0N); the orb has no half blink. */
export const FRAME = { base: 0, squash: 1, stretch: 2, happy: 3, error: 4, blink: 5, blinkHalf: 6 } as const;
export type FrameIndex = (typeof FRAME)[keyof typeof FRAME];

export interface BlinkStep {
  frame: FrameIndex;
  ms: number;
}

export interface SpriteConfig {
  kind: Kind;
  /** Square frame canvas; every frame of a type is drawn at the same origin. */
  size: number;
  frameCount: number;
  /** Body center inside the frame; instance positions refer to this pixel. */
  center: Point;
  /** Measured over all frames (squash is the widest, stretch the tallest). */
  extent: Extent;
  /** travel: a bright pixel flies from the parent to the slot; fade: fades in at the slot. */
  spawn: 'travel' | 'fade';
  /** Travel or fade time before the landing pop. */
  spawnLeadMs: number;
  blink: readonly BlinkStep[];
  blinkEveryMs: readonly [number, number];
  sparkles: boolean;
  /** Palette variants, picked by the parent orange's index; 1 means original colors only. */
  variantCount: number;
  /** Error look recolors the sprite (orb) or only swaps the face (orange). */
  errorRecolor: boolean;
}

export const SPRITES: Record<Kind, SpriteConfig> = {
  orange: {
    kind: 'orange',
    size: 32,
    frameCount: 7,
    center: { x: 17, y: 20 },
    extent: { left: 12, right: 11, top: 12, bottom: 9 },
    spawn: 'fade',
    spawnLeadMs: 250,
    blink: [
      { frame: FRAME.blinkHalf, ms: 60 },
      { frame: FRAME.blink, ms: 60 },
      { frame: FRAME.blinkHalf, ms: 60 },
    ],
    blinkEveryMs: [3000, 7000],
    sparkles: false,
    variantCount: 1,
    errorRecolor: false,
  },
  orb: {
    kind: 'orb',
    size: 24,
    frameCount: 6,
    center: { x: 12, y: 12 },
    extent: { left: 7, right: 6, top: 7, bottom: 6 },
    spawn: 'travel',
    spawnLeadMs: 400,
    blink: [{ frame: FRAME.blink, ms: 120 }],
    blinkEveryMs: [3000, 6000],
    sparkles: true,
    variantCount: 4, // original, blue, green, purple
    errorRecolor: true,
  },
};

/** Most oranges on stage, and most orbs per orange. */
export const LIMITS = {
  oranges: 4,
  orbsPerOrange: 8,
} as const;

/** Lifecycle timings shared by both types. */
export const TIMING = {
  landStretchMs: 80,
  landSquashMs: 80,
  bobStepMs: 500,
  hopEveryMs: [3000, 5000],
  hopStretchMs: 100,
  hopTopMs: 100,
  hopSquashMs: 80,
  completeMs: 700,
  completeHopAtMs: 120,
  completeHopMs: [80, 100, 80, 80], // up 1, up 2, up 1, squash on landing
  shakeMs: 300,
  shakeStepMs: 60,
  dissolveMs: 600,
  sparkleMs: [200, 900],
  glideMs: 120,
} as const;

/** "Never lag behind reality". */
export const LAG = {
  /** Displayed state may trail the latest event of an instance by at most this much. */
  budgetMs: 300,
  /** Complete or error while spawning: the rest of the spawn is skipped and the exit fits in this. */
  coalescedExitMs: 250,
  maxTimeScale: 3,
  /** Spawns with the same timestamp start apart by a random 0..max delay. */
  spawnJitterMs: 40,
  /** An event still not shown after this long speeds its instance up, reaching maxTimeScale at budgetMs. */
  catchUpFromMs: 150,
  /** The debug readout keeps the worst lag for this long. */
  peakHoldMs: 3000,
} as const;

/** Demo agent simulator (sim.ts): bursts, rapid serial runs and random churn. */
export const SIM = {
  burstCount: [3, 6],
  serialCount: 6,
  serialLifeMs: [150, 600],
  /** Normal long-running sub-agents of the churn. */
  longLifeMs: [5000, 30000],
  /** Sub-agents of a churn burst. */
  burstLifeMs: [3000, 15000],
  churnStepMs: [1500, 4000],
  /** Churn step mix: burst, rapid serial run, otherwise one long-running orb. */
  churnBurstShare: 0.25,
  churnSerialShare: 0.25,
  /** Share of churn sub-agents that complete; the rest error. */
  completeShare: 0.8,
  /** Errored churn sub-agents are dismissed after this long. */
  dismissErrorAfterMs: 4000,
} as const;

/** Dissolve particle speeds, sprite pixels per second. */
export const PARTICLE = {
  outward: [6, 14],
  upward: [14, 26],
  jitter: 4,
} as const;

export const SPARKLE = {
  count: [3, 5],
  /** Ring distance from the orb center: body radius 7 plus 2-3 px. */
  distance: [9, 10],
} as const;

/**
 * The pet in pet-stage pixels, measured over the idle frame, every state body and the hair swing
 * (hair_c, hair_l, hair_r) of assets/runtime. Update these when the pet sprite changes.
 */
export const PET = {
  headTop: { x: 60, y: 26 },
  /** Inclusive silhouette bounds. */
  body: { left: 12, right: 116, top: 26, bottom: 149 },
  shoulderY: 86,
  waistY: 112,
} as const;

export const LAYOUT = {
  /** Demo stage and where the pet stage's top-left sits in it. */
  stage: { w: 200, h: 232 },
  petOrigin: { x: 40, y: 72 },
  /** Free sprite pixels between an orange and the pet's body bounds. */
  orangeGap: 8,
  /** Free sprite pixels between the head top and the lowest orb of the middle of the first arc. */
  arcGap: 6,
  arcPerRow: 8,
  /** Horizontal distance between neighboring orbs of one arc. */
  arcStepX: 20,
  /** Each further arc sits this much higher. */
  arcRowStep: 20,
  /** How much lower the outermost slot of a full arc sits than the middle one. */
  arcSag: 10,
} as const;
