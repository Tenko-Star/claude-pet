// Load-time preparation. Orb frames are decoded once, palette-swapped into every variant and
// the error look, and given an outer-shadow canvas; nothing is recolored per frame.
// Also loads the main character's sprites and manifest from assets/runtime (read-only).

import manifestJson from '../../../runtime/manifest.json';
import orbBase from '../../../subagent/orb/orb_00_base.png';
import orbSquash from '../../../subagent/orb/orb_01_squash.png';
import orbStretch from '../../../subagent/orb/orb_02_stretch.png';
import orbHappy from '../../../subagent/orb/orb_03_face_happy.png';
import orbError from '../../../subagent/orb/orb_04_face_error.png';
import orbBlink from '../../../subagent/orb/orb_05_blink.png';
import type { Manifest } from './pet';
import { FRAME_SIZE, VARIANT_COUNT, type FramePixel } from './state';

export const manifest = manifestJson as unknown as Manifest;

const ORB_URLS = [orbBase, orbSquash, orbStretch, orbHappy, orbError, orbBlink];
const PET_URLS = import.meta.glob<string>('../../../runtime/sprites/*.png', { eager: true, import: 'default' });

type Rgb = [number, number, number];
type Hsl = [number, number, number];

/** Never recolored: eye color and white highlight. */
const PROTECTED: Rgb[] = [[72, 40, 14], [255, 255, 255]];
const HUE_TARGETS: (number | null)[] = [null, 210, 125, 275];
const ERROR_HUE = 215;
const ERROR_MAX_SATURATION = 0.18;
const SHADOW_COLOR = 'rgba(16,12,24,0.55)';

export interface OrbLook {
  frames: HTMLCanvasElement[]; // indexed by FRAME
  colors: string[]; // css color per palette index, for particles
  bright: string; // lightest body color: traveling spawn pixel
  sparkle: string; // second-lightest body color: sparkles
}

export interface OrbSet {
  variants: OrbLook[]; // original, blue, green, purple
  error: OrbLook;
  gray: string[]; // css color per palette index, for gray dissolve particles
  shadows: HTMLCanvasElement[]; // indexed by FRAME
  pixels: FramePixel[][]; // indexed by FRAME, palette indices
}

export async function loadOrbs(): Promise<OrbSet> {
  const datas = await Promise.all(ORB_URLS.map(decode));

  // One palette shared by all frames, so a pixel's palette index means the same color everywhere.
  const palette: Rgb[] = [];
  const counts: number[] = [];
  const indexOf = new Map<number, number>();
  const pixels: FramePixel[][] = datas.map((d) => {
    const out: FramePixel[] = [];
    for (let i = 0; i < d.width * d.height; i++) {
      if (d.data[i * 4 + 3] === 0) continue;
      const rgb: Rgb = [d.data[i * 4], d.data[i * 4 + 1], d.data[i * 4 + 2]];
      const key = (rgb[0] << 16) | (rgb[1] << 8) | rgb[2];
      let idx = indexOf.get(key);
      if (idx === undefined) {
        idx = palette.length;
        indexOf.set(key, idx);
        palette.push(rgb);
        counts.push(0);
      }
      counts[idx]++;
      out.push({ x: i % d.width, y: Math.floor(i / d.width), color: idx });
    }
    return out;
  });

  const recolorable = palette.map((c) => !PROTECTED.some((p) => p[0] === c[0] && p[1] === c[1] && p[2] === c[2]));
  const hsl = palette.map(rgbToHsl);
  const dominant = dominantHue(hsl, counts, recolorable);

  const swap = (map: (c: Hsl) => Hsl): Rgb[] =>
    palette.map((c, i) => {
      if (!recolorable[i]) return c;
      const [h, s, l] = map(hsl[i]);
      return hslToRgb([((h % 360) + 360) % 360, s, l]);
    });

  if (HUE_TARGETS.length !== VARIANT_COUNT) throw new Error('HUE_TARGETS must match VARIANT_COUNT');
  const variants = HUE_TARGETS.map((target) =>
    buildLook(pixels, swap(([h, s, l]) => [target === null ? h : h + target - dominant, s, l]), hsl, recolorable),
  );
  const error = buildLook(pixels, swap(([, s, l]) => [ERROR_HUE, Math.min(s * 0.3, ERROR_MAX_SATURATION), l]), hsl, recolorable);
  const gray = hsl.map(([, , l]) => css(hslToRgb([0, 0, l])));
  const shadows = pixels.map(buildShadow);
  return { variants, error, gray, shadows, pixels };
}

export async function loadPetSprites(): Promise<Map<string, HTMLImageElement>> {
  const out = new Map<string, HTMLImageElement>();
  await Promise.all(
    Object.entries(PET_URLS).map(async ([path, url]) => {
      const img = new Image();
      img.src = url;
      await img.decode();
      out.set(path.slice(path.lastIndexOf('/') + 1), img);
    }),
  );
  return out;
}

async function decode(url: string): Promise<ImageData> {
  const img = new Image();
  img.src = url;
  await img.decode();
  if (img.naturalWidth !== FRAME_SIZE || img.naturalHeight !== FRAME_SIZE) throw new Error(`orb frame must be ${FRAME_SIZE}x${FRAME_SIZE}`);
  const ctx = canvas2d(FRAME_SIZE, FRAME_SIZE);
  ctx.drawImage(img, 0, 0);
  return ctx.getImageData(0, 0, FRAME_SIZE, FRAME_SIZE);
}

/** Saturation-weighted circular mean hue of the recolorable colors, weighted by pixel count. */
function dominantHue(hsl: Hsl[], counts: number[], recolorable: boolean[]): number {
  let sx = 0;
  let sy = 0;
  hsl.forEach(([h, s], i) => {
    if (!recolorable[i]) return;
    const rad = (h * Math.PI) / 180;
    sx += counts[i] * s * Math.cos(rad);
    sy += counts[i] * s * Math.sin(rad);
  });
  return ((Math.atan2(sy, sx) * 180) / Math.PI + 360) % 360;
}

function buildLook(pixels: FramePixel[][], colors: Rgb[], hsl: Hsl[], recolorable: boolean[]): OrbLook {
  const frames = pixels.map((list) => {
    const img = new ImageData(FRAME_SIZE, FRAME_SIZE);
    for (const p of list) img.data.set([...colors[p.color], 255], (p.y * FRAME_SIZE + p.x) * 4);
    const ctx = canvas2d(FRAME_SIZE, FRAME_SIZE);
    ctx.putImageData(img, 0, 0);
    return ctx.canvas;
  });
  // Lightness order is preserved by the swap, so rank on the original lightness.
  const byLight = colors.map((_, i) => i).filter((i) => recolorable[i]).sort((a, b) => hsl[b][2] - hsl[a][2]);
  const pick = (rank: number): string => css(colors[byLight[Math.min(rank, byLight.length - 1)]]);
  return { frames, colors: colors.map(css), bright: pick(0), sparkle: pick(1) };
}

/** 1 px ring of transparent pixels 8-adjacent to the silhouette. */
function buildShadow(list: FramePixel[]): HTMLCanvasElement {
  const n = FRAME_SIZE;
  const solid = new Uint8Array(n * n);
  for (const p of list) solid[p.y * n + p.x] = 1;
  const ctx = canvas2d(n, n);
  ctx.fillStyle = SHADOW_COLOR;
  for (let y = 0; y < n; y++) {
    for (let x = 0; x < n; x++) {
      if (solid[y * n + x]) continue;
      let near = false;
      for (let dy = -1; dy <= 1 && !near; dy++) {
        for (let dx = -1; dx <= 1 && !near; dx++) {
          const nx = x + dx;
          const ny = y + dy;
          near = nx >= 0 && ny >= 0 && nx < n && ny < n && solid[ny * n + nx] === 1;
        }
      }
      if (near) ctx.fillRect(x, y, 1, 1);
    }
  }
  return ctx.canvas;
}

function canvas2d(w: number, h: number): CanvasRenderingContext2D {
  const canvas = document.createElement('canvas');
  canvas.width = w;
  canvas.height = h;
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('2D canvas unavailable');
  return ctx;
}

function css([r, g, b]: Rgb): string {
  return `rgb(${r},${g},${b})`;
}

function rgbToHsl([r8, g8, b8]: Rgb): Hsl {
  const r = r8 / 255;
  const g = g8 / 255;
  const b = b8 / 255;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const l = (max + min) / 2;
  if (max === min) return [0, 0, l];
  const d = max - min;
  const s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
  let h: number;
  if (max === r) h = (g - b) / d + (g < b ? 6 : 0);
  else if (max === g) h = (b - r) / d + 2;
  else h = (r - g) / d + 4;
  return [h * 60, s, l];
}

function hslToRgb([h, s, l]: Hsl): Rgb {
  const c = (1 - Math.abs(2 * l - 1)) * s;
  const hp = h / 60;
  const x = c * (1 - Math.abs((hp % 2) - 1));
  let rgb: Rgb;
  if (hp < 1) rgb = [c, x, 0];
  else if (hp < 2) rgb = [x, c, 0];
  else if (hp < 3) rgb = [0, c, x];
  else if (hp < 4) rgb = [0, x, c];
  else if (hp < 5) rgb = [x, 0, c];
  else rgb = [c, 0, x];
  const m = l - c / 2;
  return [Math.round((rgb[0] + m) * 255), Math.round((rgb[1] + m) * 255), Math.round((rgb[2] + m) * 255)];
}
