import { loadOrbs, loadPetSprites, manifest } from './frames';
import { createPet, goState, PACE_LABELS, react, setAuto, setPace, STATE_LABELS, updatePet } from './pet';
import { ANCHOR, drawStage, STAGE, type Background, type View } from './render';
import { complete, createWorld, dismiss, fail, hitTest, liveCount, MAX_COUNT, setChurn, setCount, update } from './state';

const INITIAL_COUNT = 3;
const PET_SEED = 7;
const WORLD_SEED = 20251004;
const DEBUG_EVERY_MS = 200;
const VARIANT_NAMES = ['原色', '蓝', '绿', '紫'];

function el<T extends HTMLElement>(id: string): T {
  const e = document.getElementById(id);
  if (!e) throw new Error(`#${id} missing`);
  return e as T;
}

function radios(name: string): HTMLInputElement[] {
  return Array.from(document.querySelectorAll<HTMLInputElement>(`input[name="${name}"]`));
}

function checkedValue(name: string): string {
  return radios(name).find((r) => r.checked)?.value ?? '';
}

/** One canvas pixel per device pixel, so integer scales stay crisp on high-DPI screens. */
function sizeCanvas(canvas: HTMLCanvasElement, scale: number): void {
  const w = STAGE.w * scale;
  const h = STAGE.h * scale;
  if (canvas.width !== w || canvas.height !== h) {
    canvas.width = w;
    canvas.height = h;
  }
  const dpr = window.devicePixelRatio || 1;
  const cssW = `${w / dpr}px`;
  const cssH = `${h / dpr}px`;
  if (canvas.style.width !== cssW) canvas.style.width = cssW;
  if (canvas.style.height !== cssH) canvas.style.height = cssH;
}

function makeButton(label: string, sub: string, onClick: () => void): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.append(label);
  const small = document.createElement('small');
  small.textContent = sub;
  b.append(small);
  b.addEventListener('click', onClick);
  return b;
}

async function start(): Promise<void> {
  const [orbs, petImgs] = await Promise.all([loadOrbs(), loadPetSprites()]);
  const pet = createPet(manifest, PET_SEED);
  const world = createWorld(orbs.pixels, WORLD_SEED);
  setCount(world, INITIAL_COUNT);

  const canvas = el<HTMLCanvasElement>('stage');
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('2D canvas unavailable');
  const view: View = { scale: 3, bg: { kind: 'dark' }, shadow: false };

  // Pet controls
  const auto = el<HTMLInputElement>('auto');
  const manual = (): void => {
    auto.checked = false;
    setAuto(pet, false);
  };
  const stateButtons = new Map<string, HTMLButtonElement>();
  for (const name of Object.keys(manifest.states)) {
    const [label, hook] = STATE_LABELS[name] ?? [name, ''];
    const b = makeButton(label, hook, () => {
      manual();
      goState(pet, name);
    });
    stateButtons.set(name, b);
    el('states').append(b);
  }
  const paceButtons = new Map<string, HTMLButtonElement>();
  for (const pace of Object.keys(manifest.states.working?.paces ?? {})) {
    const b = makeButton(PACE_LABELS[pace] ?? pace, pace, () => {
      manual();
      setPace(pet, pace);
    });
    paceButtons.set(pace, b);
    el('paces').append(b);
  }
  el('fail').addEventListener('click', () => react(pet, 'toolFailure'));
  auto.addEventListener('change', () => setAuto(pet, auto.checked));

  // Companion controls
  const countOut = el<HTMLOutputElement>('count');
  const minus = el<HTMLButtonElement>('minus');
  const plus = el<HTMLButtonElement>('plus');
  const churn = el<HTMLInputElement>('churn');
  const shadow = el<HTMLInputElement>('shadow');
  minus.addEventListener('click', () => setCount(world, liveCount(world) - 1));
  plus.addEventListener('click', () => setCount(world, liveCount(world) + 1));
  churn.addEventListener('change', () => setChurn(world, churn.checked));
  shadow.addEventListener('change', () => (view.shadow = shadow.checked));

  canvas.addEventListener('click', (e) => {
    const rect = canvas.getBoundingClientRect();
    const p = {
      x: Math.floor(((e.clientX - rect.left) / rect.width) * STAGE.w) - ANCHOR.x,
      y: Math.floor(((e.clientY - rect.top) / rect.height) * STAGE.h) - ANCHOR.y,
    };
    const id = hitTest(world, p);
    const c = world.companions.find((x) => x.id === id);
    if (!c) return;
    if (c.phase === 'error' || c.phase === 'erroring') dismiss(world, c.id);
    else if (e.shiftKey) fail(world, c.id);
    else complete(world, c.id);
  });

  // View controls
  const bgColor = el<HTMLInputElement>('bg-color');
  const applyBackground = (): void => {
    const v = checkedValue('bg');
    const bg: Background =
      v === 'custom' ? { kind: 'custom', color: bgColor.value } : v === 'light' ? { kind: 'light' } : v === 'checker' ? { kind: 'checker' } : { kind: 'dark' };
    view.bg = bg;
  };
  radios('scale').forEach((r) => r.addEventListener('change', () => (view.scale = Number(checkedValue('scale')) || 1)));
  radios('bg').forEach((r) => r.addEventListener('change', applyBackground));
  bgColor.addEventListener('input', () => {
    const custom = radios('bg').find((r) => r.value === 'custom');
    if (custom) custom.checked = true;
    applyBackground();
  });
  view.scale = Number(checkedValue('scale')) || view.scale;
  view.shadow = shadow.checked;
  setChurn(world, churn.checked);
  setAuto(pet, auto.checked);
  applyBackground();

  const debug = el<HTMLPreElement>('debug');
  let debugInMs = 0;
  let shownState = '';
  let shownPace = '';
  let last = performance.now();
  const frame = (now: number): void => {
    const dt = Math.min(100, Math.max(0, now - last));
    last = now;
    updatePet(pet, dt);
    update(world, dt);
    sizeCanvas(canvas, view.scale);
    drawStage(ctx, pet, petImgs, world, orbs, view);

    if (pet.name !== shownState) {
      shownState = pet.name;
      stateButtons.forEach((b, k) => b.setAttribute('aria-pressed', String(k === pet.name)));
    }
    if (pet.pace !== shownPace) {
      shownPace = pet.pace;
      paceButtons.forEach((b, k) => b.setAttribute('aria-pressed', String(k === pet.pace)));
    }
    const n = liveCount(world);
    countOut.value = String(n);
    minus.disabled = n <= 0;
    plus.disabled = n >= MAX_COUNT;

    debugInMs -= dt;
    if (debugInMs <= 0) {
      debugInMs = DEBUG_EVERY_MS;
      const lines = world.companions.map(
        (c) => `#${String(c.id).padEnd(3)} ${c.phase.padEnd(10)} ${VARIANT_NAMES[c.variant] ?? c.variant}`,
      );
      debug.textContent = lines.length ? lines.join('\n') : '（没有实例）';
    }
    requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
}

start().catch((err: unknown) => {
  el('error').textContent = `加载失败：${String(err)}`;
});
