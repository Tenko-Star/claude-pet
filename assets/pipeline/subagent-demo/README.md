# Sub-agent 伴随球演示

在真实桌宠旁边调 sub-agent 伴随球动画的一次性原型。生成的单文件页面是 `assets/preview/subagent-demo.html`，直接从磁盘双击打开即可，不需要网络。

## 构建

```
cd assets/pipeline/subagent-demo
npm install
npm run build
```

`npm run build` 先跑 `tsc --noEmit`（strict），再用 Vite + vite-plugin-singlefile 打包，所有 PNG 以 data URI 内嵌。输出目录是 `assets/preview`，`emptyOutDir` 关闭，不会删掉同目录下的其他演示页。

输入（只读，必须存在才能构建）：
- `assets/subagent/orb/orb_00_base.png` 到 `orb_05_blink.png`：小球的 6 帧，24×24，底边对齐，始终画在同一原点
- `assets/runtime/manifest.json` 和 `assets/runtime/sprites/*.png`：桌宠，和 runtime 是同一份文件，改了 manifest 重新构建即可同步

## 代码结构

- `src/state.ts`：纯状态模块，不碰 DOM。小球列表、每个实例的状态机、计时器、槽位分配，由 `update(w, dtMs)` 和显式事件 `spawn`、`complete`、`fail`、`dismiss` 驱动。`arcSlots`、`nextPhase`、`orbPose`、`hopPose`、`completePose`、`bobOffsetY` 都是小的纯函数，方便移植到 C#
- `src/pet.ts`：桌宠播放器，是 `assets/preview/state-demo.html` 内联脚本的 TS 移植，行为保持一致（七个状态、进出过渡、眨眼、特效、敲击节奏、工具失败、自动播放顺序）；用自己的时钟和带种子的随机数，不碰 DOM
- `src/frames.ts`：加载帧、换色、生成外描边阴影并缓存；同时加载桌宠图片和 manifest
- `src/render.ts`：只读状态并绘制
- `src/main.ts`：控件和 requestAnimationFrame 循环

## 小球状态

| 阶段 | 表现 |
| --- | --- |
| spawning | 一个亮像素从桌宠身体中心飞到槽位（400 ms，easeOut），落地 stretch 80 ms → squash 80 ms → base |
| idle | base 帧；每 500 ms 上下浮动 1 px（每个实例随机相位）；每 3–6 秒眨眼一次（blink 帧 120 ms）；每 3–5 秒跳一下 |
| completing | 开心脸 700 ms，期间跳一次，然后溶解 |
| erroring | 错误脸 + 错误配色，水平 ±1 px 抖动 300 ms（每 60 ms 换向） |
| error | 错误脸静止，直到被清掉 |
| dissolving | 当前帧每个不透明像素变成粒子，向上并向外飘散，600 ms 内线性淡出；从错误态清掉时粒子是灰色 |

## 假设和调整过的数值

- 主角色用真实桌宠，代替原 spec 的 48×64 灰色占位框。舞台 220×200 精灵像素，manifest 的 119×153 桌宠舞台放在 (50, 43)；小球坐标以桌宠身体中心（桌宠舞台的 (64, 88)）为原点。
- 弧形槽位：半径 80 px，以头顶为中心，每多一个小球张开 30°，最多 180°，避开头发和特效。数量变化时已有小球以 120 ms 时间常数指数滑向新槽位；溶解中的实例不占槽位。
- idle 跳跃的分段 spec 没给具体时长，取 stretch 并上移 1 px 100 ms → base 仍在上方 100 ms → squash 落地 80 ms → base。跳跃期间不浮动、不眨眼。
- 完成时的“跳一次”用开心脸完成，好让表情保持可见：第 120 ms 起依次上移 1 px 80 ms、2 px 100 ms、1 px 80 ms，再 squash 80 ms 落地，之后回到开心脸。squash 帧没有开心表情，落地那 80 ms 会显示普通眼睛。
- 错误抖动取 +1/−1 交替。
- 溶解粒子：向外 6–14 px/s，向上 14–26 px/s，再加 ±4 px/s 横向随机。
- 光点：每个小球 3–5 个，出生时随机分布在离身体中心 9–10 px 的圆环上（身体半径约 7 px，即外侧 2–3 px），各自 200–900 ms 随机开关；只在 idle 和 completing 显示，不跟着浮动。颜色取该配色第二亮的身体色，第一亮色太接近浅色背景；飞行亮像素用最亮的身体色。
- 换色：6 种颜色里眼睛色 (72,40,14) 和白色高光不变，其余 4 种整体旋转色相，使饱和度加权平均色相落到蓝 210°、绿 125°、紫 275°；饱和度和亮度不变，所以明暗顺序不变。错误配色色相 215°，饱和度压到 0.18 以下。所有帧 × 配色在加载时一次生成离屏画布。
- 外描边阴影：每帧轮廓外 8 邻接的一圈透明像素，填 `rgba(16,12,24,0.55)`，每帧预先生成一张。
- 随机增减：每 0.8–2.5 秒一次。如果有小球已在错误态停了 4 秒以上，就先清掉它，否则错误球会把 8 个名额占满（spec 没写，属于补充）；否则数量未满且（没有 idle 小球或 50% 概率）就生成一个，否则随机挑一个 idle 小球，80% 完成、20% 出错。
- 数量 − 让最新的 idle 小球完成，没有 idle 时清掉最新的错误小球；正在出生的不受影响。显示的数量不含 completing 和 dissolving。
- 点击判定范围是身体中心周围 14×14；飞行中的亮像素和溶解中的实例点不中。
- 画布 1 个像素对应 1 个设备像素（CSS 尺寸除以 devicePixelRatio），高 DPI 屏上看起来偏小，但保证清晰。
- 此演示和输入图都还没定稿，未提交到仓库。
