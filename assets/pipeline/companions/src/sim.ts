// Demo agent simulator: stands in for the hook stream. It only calls the timestamped events of state.ts,
// with each event stamped at the moment it was due, so the lag readout measures the real delay.
// Not part of the portable state logic.

import { SIM } from './sprites';
import {
  between, canSpawnOrb, complete, dismiss, error, isLive, nextRandom, oranges, spawnOrange, spawnOrb,
  type World,
} from './state';

interface Job {
  atMs: number;
  run: 'complete' | 'error' | 'dismiss' | 'serial';
  /** Orb id, or the orange id of a serial run. */
  id: number;
  /** Serial run: orbs still to start. */
  left: number;
  /** Serial run: churn runs mix in errors. */
  mixErrors: boolean;
}

export interface Sim {
  jobs: Job[];
  churn: boolean;
  churnInMs: number;
}

export function createSim(): Sim {
  return { jobs: [], churn: false, churnInMs: 0 };
}

export function setChurn(sim: Sim, on: boolean): void {
  sim.churn = on;
  sim.churnInMs = 0;
}

const pickInt = (w: World, [a, b]: readonly [number, number]): number => a + Math.floor(nextRandom(w.rng) * (b - a + 1));

/** A random live orange that can take another orb; spawns one if there is no orange at all. */
function pickOrange(w: World, atMs: number): number | null {
  if (!oranges(w).some(isLive)) spawnOrange(w, atMs);
  const open = oranges(w).filter((o) => canSpawnOrb(w, o.id));
  return open.length ? open[Math.floor(nextRandom(w.rng) * open.length)].id : null;
}

/** Ends an orb at atMs: complete, or (when mixing) error and a dismiss some seconds later. */
function scheduleEnd(sim: Sim, w: World, id: number, atMs: number, mixErrors: boolean): void {
  if (!mixErrors || nextRandom(w.rng) < SIM.completeShare) {
    sim.jobs.push({ atMs, run: 'complete', id, left: 0, mixErrors });
    return;
  }
  sim.jobs.push({ atMs, run: 'error', id, left: 0, mixErrors });
  sim.jobs.push({ atMs: atMs + SIM.dismissErrorAfterMs, run: 'dismiss', id, left: 0, mixErrors });
}

/** 3-6 orbs on one random orange in the same tick. With lifeMs they end on their own. */
export function burst(sim: Sim, w: World, lifeMs: readonly [number, number] | null): void {
  const atMs = w.timeMs;
  const parent = pickOrange(w, atMs);
  if (parent === null) return;
  const n = pickInt(w, SIM.burstCount);
  for (let k = 0; k < n; k++) {
    const id = spawnOrb(w, parent, atMs);
    if (id === null) break;
    if (lifeMs) scheduleEnd(sim, w, id, atMs + between(w.rng, lifeMs[0], lifeMs[1]), true);
  }
}

/** Six short orbs back to back on one random orange: each starts as the previous one ends. */
export function rapidSerial(sim: Sim, w: World, mixErrors: boolean): void {
  const atMs = w.timeMs;
  const parent = pickOrange(w, atMs);
  if (parent !== null) sim.jobs.push({ atMs, run: 'serial', id: parent, left: SIM.serialCount, mixErrors });
}

function longOrb(sim: Sim, w: World): void {
  const atMs = w.timeMs;
  const parent = pickOrange(w, atMs);
  const id = parent === null ? null : spawnOrb(w, parent, atMs);
  if (id !== null) scheduleEnd(sim, w, id, atMs + between(w.rng, SIM.longLifeMs[0], SIM.longLifeMs[1]), true);
}

function runJob(sim: Sim, w: World, job: Job): void {
  switch (job.run) {
    case 'complete':
      complete(w, job.id, job.atMs);
      return;
    case 'error':
      error(w, job.id, job.atMs);
      return;
    case 'dismiss':
      dismiss(w, job.id, job.atMs);
      return;
    case 'serial': {
      const id = spawnOrb(w, job.id, job.atMs);
      if (id === null) return;
      const endAt = job.atMs + between(w.rng, SIM.serialLifeMs[0], SIM.serialLifeMs[1]);
      scheduleEnd(sim, w, id, endAt, job.mixErrors);
      if (job.left > 1) sim.jobs.push({ ...job, atMs: endAt, left: job.left - 1 });
    }
  }
}

/** Call after update(w): fires every due job in time order, stamped with its due time. */
export function tickSim(sim: Sim, w: World, dtMs: number): void {
  if (sim.churn) {
    sim.churnInMs -= dtMs;
    if (sim.churnInMs <= 0) {
      sim.churnInMs = between(w.rng, SIM.churnStepMs[0], SIM.churnStepMs[1]);
      const roll = nextRandom(w.rng);
      if (roll < SIM.churnBurstShare) burst(sim, w, SIM.burstLifeMs);
      else if (roll < SIM.churnBurstShare + SIM.churnSerialShare) rapidSerial(sim, w, true);
      else longOrb(sim, w);
    }
  }
  for (;;) {
    let next = -1;
    sim.jobs.forEach((j, k) => {
      if (j.atMs <= w.timeMs && (next < 0 || j.atMs < sim.jobs[next].atMs)) next = k;
    });
    if (next < 0) return;
    const [job] = sim.jobs.splice(next, 1);
    runJob(sim, w, job);
  }
}
