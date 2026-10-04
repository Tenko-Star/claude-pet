# Agent 伴随预览

调伴随动画和布局用的一次性原型：桌宠是当前的主 agent，它派出的每个 sub-agent 是头顶弧上的一个小球；其他主 agent 每个是一个 Q 版大头，站在桌宠下半身两侧，大头不带小球。生成的单文件页面是 `assets/preview/companions.html`，直接从磁盘双击打开即可，不需要网络。正式前端是 C#，所以状态逻辑写成不碰 DOM 的纯模块，方便移植。

## 构建

```
cd assets/pipeline/companions
pnpm install
pnpm run build
```

`build` 先跑 `tsc --noEmit`（strict），再用 Vite + vite-plugin-singlefile 打包，所有 PNG 以 data URI 内嵌。输出目录是 `assets/preview`，`emptyOutDir` 关闭，不会删掉同目录下的其他演示页。

输入（只读，必须存在才能构建）：
- `assets/runtime/manifest.json` 和 `assets/runtime/sprites/*.png`：桌宠，播放逻辑和 state-demo 一致
- `assets/subagent/head_large/head_00_base.png` 到 `head_06_blink_half.png`：大头 7 帧，64×64
- `assets/subagent/orb/orb_00_base.png` 到 `orb_05_blink.png`：小球 6 帧，24×24

## 代码结构

- `src/sprites.ts`：按类型的配置（帧数、身体中心、点击范围、出生方式、眨眼序列、光点、配色数、出错是否换色、上限）和全部时长、粒子、布局常数。**所有可调数值只在这一个文件里。**
- `src/state.ts`：纯状态模块。小球和大头共用一个状态机，由 `update(w, dtMs)` 和事件 `spawn`、`complete`、`error`、`dismiss`、`setCount` 驱动；`arcSlots`、`headSlots`、`nextPhase`、`pose` 等是小的纯函数。
- `src/pet.ts`：桌宠播放器，从 `subagent-demo` 原样搬来，按 manifest 驱动。
- `src/frames.ts`：加载帧、换色、生成外描边阴影和灰色粒子色；加载桌宠图层和 manifest。
- `src/render.ts`：只读状态并绘制：桌宠 → 大头 → 小球 → 粒子。
- `src/main.ts`：控件和 requestAnimationFrame 循环。

## 状态

| 阶段 | 表现 |
| --- | --- |
| spawning | 小球：一个亮像素从桌宠身体中心飞到槽位（easeOut）；大头：在槽位淡入。之后落地 stretch → squash → base |
| idle | base 帧；每半个浮动周期上下 1 px（每个实例随机相位）；随机间隔眨眼；每 3–5 秒跳一下 |
| completing | 开心脸并跳一次，700 ms 后溶解 |
| erroring | 错误脸，水平 ±1 px 抖动；小球换成蓝灰配色，大头只换脸 |
| error | 错误脸静止，直到被清掉 |
| dissolving | 当前帧每个不透明像素变成粒子，向上并向外飘散，线性淡出后移除；从错误态清掉时粒子是灰色 |

## 常数（都在 `src/sprites.ts`）

时长（ms）：

| 常数 | 值 | 含义 |
| --- | --- | --- |
| `SPRITES.orb.spawnLeadMs` | 400 | 小球亮像素飞行 |
| `SPRITES.head.spawnLeadMs` | 250 | 大头淡入 |
| `TIMING.landStretchMs` / `landSquashMs` | 80 / 80 | 落地 stretch、squash |
| `TIMING.bobStepMs` | 500 | 浮动每步 |
| `SPRITES.orb.blink` | blink 120 | 小球眨眼 |
| `SPRITES.head.blink` | half 60 → blink 60 → half 60 | 大头眨眼 |
| `SPRITES.orb.blinkEveryMs` / `head.blinkEveryMs` | 3000–6000 / 3000–7000 | 眨眼间隔 |
| `TIMING.hopEveryMs` | 3000–5000 | idle 跳跃间隔 |
| `TIMING.hopStretchMs` / `hopTopMs` / `hopSquashMs` | 100 / 100 / 80 | idle 跳跃各段 |
| `TIMING.completeMs` | 700 | 开心脸持续 |
| `TIMING.completeHopAtMs` / `completeHopMs` | 120 / 80, 100, 80, 80 | 完成跳跃开始、各段 |
| `TIMING.shakeMs` / `shakeStepMs` | 300 / 60 | 出错抖动总长、每次换向 |
| `TIMING.dissolveMs` | 600 | 粒子淡出 |
| `TIMING.sparkleMs` | 200–900 | 光点开关间隔 |
| `TIMING.glideMs` | 120 | 滑向新槽位的指数时间常数 |
| `TIMING.churnMs` | 800–2500 | 随机增减每步间隔 |
| `TIMING.churnDismissAfterMs` | 4000 | 随机增减时出错实例停留多久后自动清掉 |

布局（精灵像素，坐标以桌宠身体中心为原点）：

| 常数 | 值 | 含义 |
| --- | --- | --- |
| `LAYOUT.stage` | 352×168 | 舞台 |
| `LAYOUT.anchor` / `petCenter` | (176, 100) / (64, 88) | 桌宠身体中心在舞台和 manifest 舞台里的位置 |
| `SPRITES.orb.max` / `head.max` | 8 / 4 | 上限 |
| `LAYOUT.arcRadius` / `arcStepDeg` / `arcMaxSpanDeg` | 80 / 30 / 150 | 小球弧半径、每多一个张开的角度、最大张角 |
| `LAYOUT.headInnerDx` / `headStepDx` | 74 / 62 | 第一对大头离中心的水平距离、往外每对再加的距离 |
| `LAYOUT.headDy` | 34 | 大头中心在身体中心下方的距离，使大头底边和桌宠脚底同一行 |
| `PARTICLE.outward` / `upward` / `jitter` | 6–14 / 14–26 / ±4 px/s | 粒子速度 |
| `SPARKLE.count` / `distance` | 3–5 / 9–10 px | 每个小球的光点数、离中心距离 |

## 假设

- 大头按 右、左、右、左 的顺序排，后两个在外侧；数量变化时已有的滑到新位置。大头画在桌宠前面，小球画在最上层。
- 小球弧最大张 150°，最外侧小球不会碰到下面的大头。
- 点击：小球和大头都是点击完成、Shift+点击出错、点击出错的清掉；小球优先于大头，飞行中的亮像素和溶解中的实例点不中。
- − 先让最新的 idle 实例完成，没有 idle 的就清掉最新的出错实例。
- 随机增减：约 75% 的步骤针对小球、25% 针对大头；出错实例停留 4 秒以上先清掉。
- 小球按生成顺序轮换 原色/蓝/绿/紫 四种配色（眼睛色 (72,40,14) 和白色不变），大头不换色、没有光点。
- 画布 1 个像素对应 1 个设备像素，高 DPI 屏上看起来偏小，但保证清晰。默认 2x。
- 此演示和输入图都还没定稿，未提交到仓库。
