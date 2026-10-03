declare const LAYERS: Record<string, string>;

type Layer = "main" | "eye_half" | "eye_closed" | "mouth_open";
const W = 119, H = 129;

const canvas = document.getElementById("stage") as HTMLCanvasElement;
const ctx = canvas.getContext("2d")!;
canvas.width = W; canvas.height = H;
ctx.imageSmoothingEnabled = false;

const imgs = new Map<string, HTMLImageElement>();
await Promise.all(Object.entries(LAYERS).map(([k, src]) => new Promise<void>((res) => {
  const im = new Image(); im.onload = () => res(); im.src = src; imgs.set(k, im);
})));

const opts = { sway: true, blink: true, talk: false, big: false };
// 后发摆动：中 → 左 → 中 → 右，逐帧切换差分图
const SWAY_SMALL = ["hair_c", "hair_l", "hair_c", "hair_r"];
const SWAY_BIG = ["hair_c", "hair_l_big", "hair_c", "hair_r"];
let frameMs = 350;
// 眨眼：半闭 → 闭 → 半闭，单位毫秒
const BLINK: [Layer | null, number][] = [["eye_half", 60], ["eye_closed", 90], ["eye_half", 60]];
let nextBlink = performance.now() + 2000;

function eyeAt(now: number): Layer | null {
  if (!opts.blink) return null;
  if (now < nextBlink) return null;
  let t = now - nextBlink;
  for (const [layer, dur] of BLINK) { if (t < dur) return layer; t -= dur; }
  nextBlink = now + 2500 + Math.random() * 3000;
  return null;
}

function draw(now: number) {
  ctx.clearRect(0, 0, W, H);
  const seq = opts.big ? SWAY_BIG : SWAY_SMALL;
  const hair = opts.sway ? seq[Math.floor(now / frameMs) % seq.length] : "hair_c";
  ctx.drawImage(imgs.get(hair)!, 0, 0);
  ctx.drawImage(imgs.get("main")!, 0, 0);
  const eye = eyeAt(now);
  if (eye) ctx.drawImage(imgs.get(eye)!, 0, 0);
  if (opts.talk && Math.floor(now / 140) % 2 === 0) ctx.drawImage(imgs.get("mouth_open")!, 0, 0);
  requestAnimationFrame(draw);
}
requestAnimationFrame(draw);

document.querySelectorAll<HTMLButtonElement>("[data-opt]").forEach((b) => {
  const key = b.dataset.opt as keyof typeof opts;
  b.setAttribute("aria-pressed", String(opts[key]));
  b.addEventListener("click", () => {
    opts[key] = !opts[key];
    b.setAttribute("aria-pressed", String(opts[key]));
  });
});
document.querySelectorAll<HTMLButtonElement>("[data-bg]").forEach((b) =>
  b.addEventListener("click", () => {
    document.querySelector(".stage-wrap")!.className = "stage-wrap " + b.dataset.bg;
    document.querySelectorAll("[data-bg]").forEach((x) => x.setAttribute("aria-pressed", String(x === b)));
  }));
const scale = document.getElementById("scale") as HTMLInputElement;
const applyScale = () => {
  canvas.style.width = `${W * Number(scale.value)}px`;
  document.getElementById("scaleOut")!.textContent = `${scale.value}×`;
};
scale.addEventListener("input", applyScale); applyScale();

const speed = document.getElementById("speed") as HTMLInputElement;
const applySpeed = () => {
  frameMs = Number(speed.value);
  document.getElementById("speedOut")!.textContent = `${speed.value}ms`;
};
speed.addEventListener("input", applySpeed); applySpeed();

const sheet = document.getElementById("sheet")!;
for (const k of ["main", "hair_c", "hair_l", "hair_r", "hair_l_big", "eye_half", "eye_closed", "mouth_open"]) {
  const fig = document.createElement("figure");
  const im = new Image(); im.src = LAYERS[k]; im.alt = k;
  const cap = document.createElement("figcaption"); cap.textContent = k;
  fig.append(im, cap); sheet.append(fig);
}
