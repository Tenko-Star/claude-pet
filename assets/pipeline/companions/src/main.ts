import { loadPetSprites, loadSprites, manifest } from './frames';
import { createPet, goState, PACE_LABELS, react, setAuto, setPace, STATE_LABELS, updatePet } from './pet';
import { drawStage, STAGE, type Background, type View } from './render';
import { burst, createSim, rapidSerial, setChurn, tickSim } from './sim';
import { LAG, LAYOUT, LIMITS, type Kind } from './sprites';
import {
  canSpawnOrb, children, complete, createWorld, currentLagMs, dismiss, error, freeOrangeIndex, hitTest, isLive,
  orangeAt, oranges, removeOrange, spawnOrange, spawnOrb, update, holdsSlot, type Instance, type World,
} from './state';

const INITIAL_ORANGES = 2;
const INITIAL_ORBS_PER_ORANGE = 2;
const PET_SEED = 7;
const WORLD_SEED = 20261004;
const DEBUG_EVERY_MS = 200;
const VARIANT_NAMES = ['原色', '蓝', '绿', '紫'];
const KIND_NAMES: Record<Kind, string> = { orange: '橘子', orb: '小球' };

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

function plainButton(label: string, aria: string, onClick: () => void): HTMLButtonElement {
  const b = document.createElement('button');
  b.type = 'button';
  b.textContent = label;
  b.setAttribute('aria-label', aria);
  b.addEventListener('click', onClick);
  return b;
}

const canEnd = (i: Instance): boolean => i.phase === 'spawning' || i.phase === 'idle';

/** − on an orange's orbs: complete the newest running orb, else dismiss the newest errored one. */
function trimOrb(w: World, orangeId: number): void {
  const live = children(w, orangeId).filter(isLive);
  const running = live.filter(canEnd);
  if (running.length) {
    complete(w, running[running.length - 1].id, w.timeMs);
    return;
  }
  const last = live[live.length - 1];
  if (last) dismiss(w, last.id, w.timeMs);
}

interface OrangeRow {
  root: HTMLDivElement;
  count: HTMLOutputElement;
  minus: HTMLButtonElement;
  plus: HTMLButtonElement;
  done: HTMLButtonElement;
  fail: HTMLButtonElement;
}

async function start(): Promise<void> {
  const [sets, petImgs] = await Promise.all([loadSprites(), loadPetSprites()]);
  const pet = createPet(manifest, PET_SEED);
  const world = createWorld({ orange: sets.orange.pixels, orb: sets.orb.pixels }, WORLD_SEED);
  const sim = createSim();
  for (let n = 0; n < INITIAL_ORANGES; n++) {
    const id = spawnOrange(world, 0);
    for (let k = 0; id !== null && k < INITIAL_ORBS_PER_ORANGE; k++) spawnOrb(world, id, 0);
  }

  const canvas = el<HTMLCanvasElement>('stage');
  const ctx = canvas.getContext('2d');
  if (!ctx) throw new Error('2D canvas unavailable');
  const view: View = { scale: 2, bg: { kind: 'dark' }, shadow: false, hoverOrange: null };

  // Main agent (the pet)
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

  // Oranges: count, then one row per orange slot
  const orangeCount = el<HTMLOutputElement>('orange-count');
  const orangeMinus = el<HTMLButtonElement>('orange-minus');
  const orangePlus = el<HTMLButtonElement>('orange-plus');
  orangePlus.addEventListener('click', () => spawnOrange(world, world.timeMs));
  orangeMinus.addEventListener('click', () => {
    const newest = oranges(world).filter(holdsSlot).pop();
    if (newest) removeOrange(world, newest.id, world.timeMs);
  });

  const rows: OrangeRow[] = [];
  for (let index = 0; index < LIMITS.oranges; index++) {
    const withOrange = (f: (o: Instance) => void) => (): void => {
      const o = orangeAt(world, index);
      if (o) f(o);
    };
    const root = document.createElement('div');
    root.className = 'orow';
    const name = document.createElement('span');
    name.className = 'name';
    const sw = document.createElement('span');
    sw.className = 'sw';
    sw.style.background = sets.orb.variants[index % sets.orb.variants.length].body;
    name.append(sw, `橘子 ${index + 1}`);
    const count = document.createElement('output');
    const minus = plainButton('−', `橘子 ${index + 1} 减少小球`, withOrange((o) => trimOrb(world, o.id)));
    const plus = plainButton('+', `橘子 ${index + 1} 增加小球`, withOrange((o) => spawnOrb(world, o.id, world.timeMs)));
    const done = plainButton('完成', `橘子 ${index + 1} 完成`, withOrange((o) => complete(world, o.id, world.timeMs)));
    const fail = plainButton('出错', `橘子 ${index + 1} 出错`, withOrange((o) => error(world, o.id, world.timeMs)));
    const label = document.createElement('span');
    label.textContent = '小球';
    root.append(name, label, minus, count, plus, done, fail);
    el('orange-rows').append(root);
    rows.push({ root, count, minus, plus, done, fail });
  }

  el('burst').addEventListener('click', () => burst(sim, world, null));
  el('serial').addEventListener('click', () => rapidSerial(sim, world, false));
  const churn = el<HTMLInputElement>('churn');
  const shadow = el<HTMLInputElement>('shadow');
  churn.addEventListener('change', () => setChurn(sim, churn.checked));
  shadow.addEventListener('change', () => (view.shadow = shadow.checked));

  const toWorld = (e: MouseEvent): { x: number; y: number } => {
    const rect = canvas.getBoundingClientRect();
    return {
      x: Math.floor(((e.clientX - rect.left) / rect.width) * STAGE.w) - LAYOUT.petOrigin.x,
      y: Math.floor(((e.clientY - rect.top) / rect.height) * STAGE.h) - LAYOUT.petOrigin.y,
    };
  };
  canvas.addEventListener('click', (e) => {
    const i = hitTest(world, toWorld(e));
    if (!i) return;
    if (i.phase === 'error' || i.phase === 'erroring') dismiss(world, i.id, world.timeMs);
    else if (i.kind === 'orb' && e.shiftKey) error(world, i.id, world.timeMs);
    else if (i.kind === 'orb') complete(world, i.id, world.timeMs);
  });
  canvas.addEventListener('mousemove', (e) => {
    const i = hitTest(world, toWorld(e));
    view.hoverOrange = i && i.kind === 'orange' ? i.id : null;
  });
  canvas.addEventListener('mouseleave', () => (view.hoverOrange = null));

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
  setChurn(sim, churn.checked);
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
    tickSim(sim, world, dt);
    if (view.hoverOrange !== null && !oranges(world).some((o) => o.id === view.hoverOrange && holdsSlot(o))) view.hoverOrange = null;
    sizeCanvas(canvas, view.scale);
    drawStage(ctx, pet, petImgs, world, sets, view);

    if (pet.name !== shownState) {
      shownState = pet.name;
      stateButtons.forEach((b, k) => b.setAttribute('aria-pressed', String(k === pet.name)));
    }
    if (pet.pace !== shownPace) {
      shownPace = pet.pace;
      paceButtons.forEach((b, k) => b.setAttribute('aria-pressed', String(k === pet.pace)));
    }

    const live = oranges(world).filter(isLive).length;
    orangeCount.value = String(live);
    orangeMinus.disabled = !oranges(world).some(holdsSlot);
    orangePlus.disabled = freeOrangeIndex(world) < 0;
    rows.forEach((row, index) => {
      const o = orangeAt(world, index);
      const n = o ? children(world, o.id).filter(isLive).length : 0;
      row.root.dataset.empty = String(!o);
      row.count.value = String(n);
      row.minus.disabled = !o || n === 0;
      row.plus.disabled = !o || !canSpawnOrb(world, o.id);
      row.done.disabled = !o || !canEnd(o);
      row.fail.disabled = !o || !canEnd(o);
    });

    debugInMs -= dt;
    if (debugInMs <= 0) {
      debugInMs = DEBUG_EVERY_MS;
      const head = `延迟：当前 ${Math.round(currentLagMs(world))} ms，最近 ${LAG.peakHoldMs / 1000} 秒最大 ${Math.round(world.lagPeakMs)} ms（预算 ${LAG.budgetMs} ms）`
        + `  丢弃过期事件 ${world.droppedEvents}  粒子 ${world.particles.length}`;
      const lines = world.instances.map((i) => {
        const lag = i.pendingAtMs !== null ? world.timeMs - i.pendingAtMs : i.lagMs;
        return [
          `#${i.id}`.padEnd(5),
          KIND_NAMES[i.kind].padEnd(3),
          (i.parent === null ? '-' : `#${i.parent}`).padEnd(5),
          (i.pendingAtMs !== null ? `${i.phase}…` : i.phase).padEnd(11),
          (i.kind === 'orb' ? (VARIANT_NAMES[i.index] ?? String(i.index)) : '-').padEnd(3),
          `${i.timeScale.toFixed(1)}x`.padEnd(5),
          `${Math.round(lag)} ms`,
        ].join(' ');
      });
      debug.textContent = `${head}\nid    类型 父    状态        配色 速度  延迟\n${lines.length ? lines.join('\n') : '（没有实例）'}`;
    }
    requestAnimationFrame(frame);
  };
  requestAnimationFrame(frame);
}

start().catch((err: unknown) => {
  el('error').textContent = `加载失败：${String(err)}`;
});
