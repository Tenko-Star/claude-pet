// Read-only renderer. All positions are whole sprite pixels multiplied by an integer scale.

import type { OrbSet } from './frames';
import { petLayers, type Pet } from './pet';
import { FRAME_CENTER, orbPose, sparklesVisible, TIMING, type World } from './state';

export const STAGE = { w: 220, h: 200 } as const;
/** Top-left of the manifest's 119x153 pet stage inside the demo stage. */
export const PET_ORIGIN = { x: 50, y: 43 } as const;
/** Pet body center in stage pixels; companion coordinates are relative to this point. */
export const ANCHOR = { x: PET_ORIGIN.x + 64, y: PET_ORIGIN.y + 88 } as const;

export type Background = { kind: 'dark' } | { kind: 'light' } | { kind: 'checker' } | { kind: 'custom'; color: string };

export interface View {
  scale: number;
  bg: Background;
  shadow: boolean;
}

const CHECKER_CELL = 4; // sprite pixels

export function drawStage(
  ctx: CanvasRenderingContext2D, pet: Pet, petImgs: Map<string, HTMLImageElement>, world: World, orbs: OrbSet, view: View,
): void {
  const k = view.scale;
  ctx.imageSmoothingEnabled = false;
  ctx.globalAlpha = 1;
  drawBackground(ctx, k, view.bg);

  for (const l of petLayers(pet)) {
    const img = petImgs.get(l.name);
    if (!img) continue;
    const x = PET_ORIGIN.x + l.x;
    const y = PET_ORIGIN.y + l.y - (l.bottom ? img.height : 0);
    ctx.drawImage(img, x * k, y * k, img.width * k, img.height * k);
  }

  for (const c of world.companions) {
    const pose = orbPose(c, world.timeMs);
    if (pose.visual === 'none') continue;
    const look = pose.palette === 'error' ? orbs.error : orbs.variants[c.variant % orbs.variants.length];
    const cx = ANCHOR.x + Math.floor(c.pos.x);
    const cy = ANCHOR.y + Math.floor(c.pos.y);
    if (pose.visual === 'pixel') {
      ctx.fillStyle = look.bright;
      ctx.fillRect(cx * k, cy * k, k, k);
      continue;
    }
    const x = cx + pose.dx - FRAME_CENTER.x;
    const y = cy + pose.dy - FRAME_CENTER.y;
    if (view.shadow) drawCanvas(ctx, orbs.shadows[pose.frame], x, y, k);
    drawCanvas(ctx, look.frames[pose.frame], x, y, k);
    if (sparklesVisible(c.phase)) {
      ctx.fillStyle = look.sparkle;
      for (const s of c.sparkles) if (s.on) ctx.fillRect((cx + s.x) * k, (cy + s.y) * k, k, k);
    }
  }

  for (const p of world.particles) {
    ctx.globalAlpha = Math.max(0, 1 - p.ageMs / TIMING.dissolveMs);
    ctx.fillStyle = p.palette === 'gray' ? orbs.gray[p.color] : orbs.variants[p.variant % orbs.variants.length].colors[p.color];
    ctx.fillRect((ANCHOR.x + Math.floor(p.x)) * k, (ANCHOR.y + Math.floor(p.y)) * k, k, k);
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
