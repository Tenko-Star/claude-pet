// Read-only renderer. All positions are whole sprite pixels multiplied by an integer scale.
// The pet and every companion share that one scale.

import type { SpriteSet } from './frames';
import { petLayers, type Pet } from './pet';
import { LAYOUT, SPRITES, TIMING, type Kind } from './sprites';
import { BRIGHT_COLOR, pose, sparklesVisible, type Instance, type World } from './state';

export const STAGE = LAYOUT.stage;
/** Top-left of the pet stage (the coordinate origin of the world) inside the demo stage. */
const ORIGIN = LAYOUT.petOrigin;

/** Alpha of the orbs of other oranges while an orange is hovered. */
const HOVER_DIM_ALPHA = 0.5;
const CHECKER_CELL = 4; // sprite pixels

export type Background = { kind: 'dark' } | { kind: 'light' } | { kind: 'checker' } | { kind: 'custom'; color: string };

export interface View {
  scale: number;
  bg: Background;
  shadow: boolean;
  /** Hovered orange: its orbs are outlined, the others dimmed. */
  hoverOrange: number | null;
}

export function drawStage(
  ctx: CanvasRenderingContext2D, pet: Pet, petImgs: Map<string, HTMLImageElement>,
  world: World, sets: Record<Kind, SpriteSet>, view: View,
): void {
  const k = view.scale;
  ctx.imageSmoothingEnabled = false;
  ctx.globalAlpha = 1;
  drawBackground(ctx, k, view.bg);

  for (const l of petLayers(pet)) {
    const img = petImgs.get(l.name);
    if (!img) continue;
    const x = ORIGIN.x + l.x;
    const y = ORIGIN.y + l.y - (l.bottom ? img.height : 0);
    ctx.drawImage(img, x * k, y * k, img.width * k, img.height * k);
  }

  // Oranges beside the pet; orbs and their spawn pixels on top.
  for (const i of world.instances) if (i.kind === 'orange') drawInstance(ctx, world, i, sets.orange, view);
  for (const i of world.instances) if (i.kind === 'orb') drawInstance(ctx, world, i, sets.orb, view);

  for (const p of world.particles) {
    const set = sets[p.kind];
    const look = set.variants[p.variant % set.variants.length];
    ctx.globalAlpha = Math.max(0, 1 - p.ageMs / TIMING.dissolveMs);
    ctx.fillStyle = p.color === BRIGHT_COLOR ? look.bright : p.look === 'gray' ? set.gray[p.color] : look.colors[p.color];
    ctx.fillRect((ORIGIN.x + Math.floor(p.x)) * k, (ORIGIN.y + Math.floor(p.y)) * k, k, k);
  }
  ctx.globalAlpha = 1;
}

function drawInstance(ctx: CanvasRenderingContext2D, world: World, i: Instance, set: SpriteSet, view: View): void {
  const k = view.scale;
  const cfg = SPRITES[i.kind];
  const p = pose(i, world.timeMs);
  if (p.visual === 'none') return;
  const look = p.palette === 'error' ? set.error : set.variants[i.index % set.variants.length];
  const hovered = i.kind === 'orb' && view.hoverOrange !== null && i.parent === view.hoverOrange;
  const dim = i.kind === 'orb' && view.hoverOrange !== null && !hovered ? HOVER_DIM_ALPHA : 1;
  const ax = ORIGIN.x + Math.floor(i.pos.x);
  const ay = ORIGIN.y + Math.floor(i.pos.y);
  if (p.visual === 'pixel') {
    ctx.globalAlpha = dim;
    ctx.fillStyle = look.bright;
    ctx.fillRect(ax * k, ay * k, k, k);
    ctx.globalAlpha = 1;
    return;
  }
  const x = ax + p.dx - cfg.center.x;
  const y = ay + p.dy - cfg.center.y;
  ctx.globalAlpha = p.alpha * dim;
  if (hovered) drawCanvas(ctx, set.highlights[p.frame], x, y, k);
  else if (view.shadow) drawCanvas(ctx, set.shadows[p.frame], x, y, k);
  drawCanvas(ctx, look.frames[p.frame], x, y, k);
  if (cfg.sparkles && sparklesVisible(i.phase)) {
    ctx.fillStyle = look.sparkle;
    for (const s of i.sparkles) if (s.on) ctx.fillRect((ax + s.x) * k, (ay + s.y) * k, k, k);
  }
  ctx.globalAlpha = 1;
}

function drawCanvas(ctx: CanvasRenderingContext2D, src: HTMLCanvasElement, x: number, y: number, k: number): void {
  ctx.drawImage(src, x * k, y * k, src.width * k, src.height * k);
}

function drawBackground(ctx: CanvasRenderingContext2D, k: number, bg: Background): void {
  const w = STAGE.w * k;
  const h = STAGE.h * k;
  if (bg.kind === 'checker') {
    const cell = CHECKER_CELL * k;
    for (let y = 0; y < h; y += cell) {
      for (let x = 0; x < w; x += cell) {
        ctx.fillStyle = ((x + y) / cell) % 2 === 0 ? '#d4d4d4' : '#f4f4f4';
        ctx.fillRect(x, y, cell, cell);
      }
    }
    return;
  }
  ctx.fillStyle = bg.kind === 'dark' ? '#282c34' : bg.kind === 'light' ? '#ececec' : bg.color;
  ctx.fillRect(0, 0, w, h);
}
