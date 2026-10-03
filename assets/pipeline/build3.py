"""第三版流程：思考、工作中、等待确认、完成、错误五个状态的整张替换主体，睡眼补丁，以及各状态的特效小图。

用法：python assets/pipeline/build3.py （在哪个目录运行都行，路径按脚本位置推算）
依赖：numpy、pillow、scipy
输入：source_images/12_notice_mid.png、13_notice.png、14_working.png、15_think.png、16_think_mid.png、17_done.png、18_sleep.png、19_error.png，
      pipeline/palette.npy、pipeline/lr2.npz、runtime/sprites/main.png
输出：runtime/sprites/main_*.png（各状态主体）、eye_sleep.png（睡眼补丁）、fx_*.png（特效）
"""
from pathlib import Path
import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
SRC, PIPE, OUT = ROOT / "source_images", ROOT / "pipeline", ROOT / "runtime" / "sprites"
PAL = np.load(PIPE / "palette.npy")
BOX = (13, 19, 132, 148)  # 与 build2.py 相同的裁剪框，得到 119×129


# ---------- 网格还原（同 grid.py） ----------
def fit(prof):
    x = np.arange(len(prof)) + 0.5
    best = (0, 0, 0)
    for p in np.arange(6.0, 10.0, 0.002):
        z = (prof * np.exp(2j * np.pi * x / p)).sum()
        if abs(z) > best[0]:
            best = (abs(z), p, np.angle(z))
    p = best[1]
    return p, (-best[2] / (2 * np.pi) * p) % p


def lowres(arr):
    g = arr.astype(float).sum(2)
    px, ox = fit(np.abs(np.diff(g, axis=1)).sum(0))
    py, oy = fit(np.abs(np.diff(g, axis=0)).sum(1))
    nx, ny = int((arr.shape[1] - ox) // px), int((arr.shape[0] - oy) // py)
    out = np.zeros((ny, nx, 3), np.uint8)
    for j in range(ny):
        y0 = int(round(oy + j * py + py * 0.25)); y1 = max(y0 + 1, int(round(oy + (j + 1) * py - py * 0.25)))
        for i in range(nx):
            x0 = int(round(ox + i * px + px * 0.25)); x1 = max(x0 + 1, int(round(ox + (i + 1) * px - px * 0.25)))
            out[j, i] = np.median(arr[y0:y1, x0:x1].reshape(-1, 3), 0)
    return out


def isbg(a):
    a = a.astype(int)
    return (a[..., 0] > 130) & (a[..., 2] > 130) & (a[..., 1] < 140) & (a[..., 2] - a[..., 1] > 60)


def quant(a):
    C = PAL.astype(float)
    a = a.copy(); ai = a.astype(int)
    a[(ai[..., 2] - ai[..., 1] > 40) & (ai[..., 0] - ai[..., 1] > 40)] = [255, 0, 255]
    q = C[((a.astype(float)[..., None, :] - C[None, None]) ** 2).sum(3).argmin(2)].astype(np.uint8)
    out = np.zeros((*a.shape[:2], 4), np.uint8)
    out[..., :3] = q; out[..., 3] = np.where(isbg(a), 0, 255)
    return out


def crop(x):
    return x[BOX[1]:BOX[3], BOX[0]:BOX[2]]


def key(x):
    return np.where(x[..., 3:] > 0, x[..., :3].astype(int), -999)


# ---------- 整张替换主体 ----------
NB = np.load(PIPE / "lr2.npz")["nb"]                         # 07 号图按基准网格采样的结果
MAIN = np.array(Image.open(OUT / "main.png").convert("RGBA"))
Y, X = np.mgrid[:129, :119]


def body(src, allow, min_region=60):
    """把 AI 图对齐到主体网格，只在 allow 范围内的成片变化区域取新像素，其余沿用 main。"""
    lr = lowres(np.array(Image.open(src).convert("RGB")))
    best = None
    for dy in range(-4, 5):
        for dx in range(-4, 5):
            s = np.roll(np.roll(lr, dy, 0), dx, 1)[:NB.shape[0], :NB.shape[1]]
            A, B = ~isbg(NB), ~isbg(s)
            iou = (A & B).sum() / (A | B).sum()
            if best is None or iou > best[0]:
                best = (iou, dx, dy, s)
    print(f"  {src.name}: 偏移 ({best[1]},{best[2]})  轮廓重合 {best[0]:.3f}")
    q = crop(quant(best[3]))
    diff = np.abs(key(q) - key(MAIN)).sum(2) > 0
    lab, n = ndimage.label(ndimage.binary_opening(diff, iterations=1))   # 开运算去掉边缘噪声
    sizes = ndimage.sum(np.ones_like(diff), lab, range(1, n + 1))
    keep = np.isin(lab, [i + 1 for i, s in enumerate(sizes) if s >= min_region])
    keep = ndimage.binary_fill_holes(ndimage.binary_closing(ndimage.binary_dilation(keep, iterations=3), iterations=2))
    keep &= allow
    m = MAIN.copy(); m[keep] = q[keep]
    a = m[..., 3] > 0
    nn = sum(np.roll(np.roll(a, y, 0), x, 1) for y in (-1, 0, 1) for x in (-1, 0, 1)) - a
    m[a & (nn <= 1), 3] = 0                                                  # 去孤立像素
    return m


ALLOW_NOTICE = (Y >= 36) & (Y <= 95) & (X <= 72) & ~((X >= 50) & (Y < 50))  # 左臂和膝上，避开脸
ALLOW_WORK = (Y >= 45) & (Y <= 100) & (X >= 25) & (X <= 90)                 # 电脑和双臂
HAND = (61, 67, 69, 78)                                                      # 打字手部块 x0,x1,y0,y1
EYES = (X >= 41) & (X <= 69) & (Y >= 29) & (Y <= 42)
ALLOW_THINK = (Y >= 36) & (Y <= 95) & (X <= 72) & ~((X >= 43) & (Y <= 40))  # 左臂、手指到嘴边，避开眼睛
ALLOW_DONE = ((Y >= 45) & (Y <= 101) & (X >= 25) & (X <= 90)) | EYES        # 双臂加眯眼
ALLOW_ERROR = (Y >= 28) & (Y <= 101) & (X >= 25) & (X <= 90)               # 双手捂脸加表情，避开头顶发饰


def aligned(src):
    """对齐到主体网格并量化，返回 119×129 的 RGBA。"""
    lr0 = lowres(np.array(Image.open(src).convert("RGB")))
    lr = np.full((NB.shape[0] + 4, NB.shape[1] + 4, 3), [255, 0, 255], np.uint8)   # 尺寸不足时用背景色补齐
    h, w = min(lr0.shape[0], lr.shape[0]), min(lr0.shape[1], lr.shape[1])
    lr[:h, :w] = lr0[:h, :w]
    best = None
    for dy in range(-4, 5):
        for dx in range(-4, 5):
            s = np.roll(np.roll(lr, dy, 0), dx, 1)[:NB.shape[0], :NB.shape[1]]
            A, B = ~isbg(NB), ~isbg(s)
            iou = (A & B).sum() / (A | B).sum()
            if best is None or iou > best[0]:
                best = (iou, dx, dy, s)
    print(f"  {src.name}: 偏移 ({best[1]},{best[2]})  轮廓重合 {best[0]:.3f}")
    return crop(quant(best[3]))


def patch(src, region, min_region=12):
    """只在原地变色的差分（眼睛、嘴巴）导出为补丁：成片变化的像素保留，其余透明。"""
    q = aligned(src)
    diff = (np.abs(key(q) - key(MAIN)).sum(2) > 0) & region
    lab, n = ndimage.label(ndimage.binary_opening(diff, iterations=1))
    sizes = ndimage.sum(np.ones_like(diff), lab, range(1, n + 1))
    keep = np.isin(lab, [i + 1 for i, s in enumerate(sizes) if s >= min_region])
    keep = ndimage.binary_fill_holes(ndimage.binary_dilation(keep, iterations=1)) & diff & (q[..., 3] > 0)
    p = np.zeros_like(MAIN); p[keep] = q[keep]
    return p


def build_patches():
    Image.fromarray(patch(SRC / "18_sleep.png", (X >= 41) & (X <= 69) & (Y >= 29) & (Y <= 46))).save(OUT / "eye_sleep.png")


def build_bodies():
    out = {
        "main_notice_mid": body(SRC / "12_notice_mid.png", ALLOW_NOTICE),
        "main_notice": body(SRC / "13_notice.png", ALLOW_NOTICE),
        "main_work": body(SRC / "14_working.png", ALLOW_WORK),
        "main_think": body(SRC / "15_think.png", ALLOW_THINK),
        "main_think_mid": body(SRC / "16_think_mid.png", ALLOW_THINK),
        "main_done": body(SRC / "17_done.png", ALLOW_DONE),
        "main_error": body(SRC / "19_error.png", ALLOW_ERROR),
    }
    tap = out["main_work"].copy()
    x0, x1, y0, y1 = HAND
    tap[y0 + 1:y1 + 1, x0:x1] = out["main_work"][y0:y1, x0:x1]               # 手部下移 1 格当按键帧
    out["main_work_tap"] = tap
    for k, v in out.items():
        Image.fromarray(v).save(OUT / f"{k}.png")


# ---------- 感叹号特效（手绘，颜色取自调色板） ----------
OUTLINE, FILL, HI, SH, CREAM = 8, 18, 12, 22, 13
BANG = ["..###..", ".#####.", ".#####.", ".#####.", ".#####.", ".#####.", ".#####.",
        "..###..", "..###..", "...#...", ".......", "..###..", ".#####.", "..###.."]


def bang(rows):
    f = np.array([[c == "#" for c in r] for r in rows])
    F = np.zeros((f.shape[0] + 2, f.shape[1] + 2), bool); F[1:-1, 1:-1] = f
    o = np.zeros_like(F)
    for dy in (-1, 0, 1):
        for dx in (-1, 0, 1):
            o |= np.roll(np.roll(F, dy, 0), dx, 1)
    idx = np.full(F.shape, -1); idx[o] = OUTLINE; idx[F] = FILL
    for y in range(F.shape[0]):
        xs = np.nonzero(F[y])[0]
        if len(xs) >= 3:
            idx[y, xs[0]] = HI; idx[y, xs[-1]] = SH
    ys, xs = np.nonzero(F); idx[ys.min(), xs[ys == ys.min()].min()] = CREAM
    img = np.zeros((*F.shape, 4), np.uint8); m = idx >= 0
    img[m, :3] = PAL[idx[m]]; img[m, 3] = 255
    return img


def spark():
    s = np.zeros((22, 23, 4), np.uint8)
    def px(x, y, c): s[y, x, :3] = PAL[c]; s[y, x, 3] = 255
    for i in range(4):
        px(5 - i, 10 - i, FILL); px(4 - i, 10 - i, OUTLINE); px(5 - i, 11 - i, OUTLINE)
        px(17 + i, 10 - i, FILL); px(18 + i, 10 - i, OUTLINE); px(17 + i, 11 - i, OUTLINE)
    for y in range(4):
        px(11, y, FILL); px(10, y, OUTLINE); px(12, y, OUTLINE)
    return s


def outlined(rows, fill, inner):
    """十字描边的小图形，'#' 为填充色，'o' 为中心高光。"""
    f = np.array([[c in "#o" for c in r] for r in rows]); h = np.array([[c == "o" for c in r] for r in rows])
    F = np.zeros((f.shape[0] + 2, f.shape[1] + 2), bool); F[1:-1, 1:-1] = f
    H = np.zeros_like(F); H[1:-1, 1:-1] = h
    o = F | np.roll(F, 1, 0) | np.roll(F, -1, 0) | np.roll(F, 1, 1) | np.roll(F, -1, 1)
    idx = np.full(F.shape, -1); idx[o] = OUTLINE; idx[F] = fill; idx[H] = inner
    img = np.zeros((*F.shape, 4), np.uint8); m = idx >= 0
    img[m, :3] = PAL[idx[m]]; img[m, 3] = 255
    return img


def shadowed(rows, fill=CREAM, shade=OUTLINE):
    """右下 1 像素投影的小图形，用于笔画会挤在一起的字形（Z、圆点）。"""
    f = np.array([[c == "#" for c in r] for r in rows]); h, w = f.shape
    S = np.zeros((h + 1, w + 1), bool); S[1:, 1:] = f
    F = np.zeros_like(S); F[:h, :w] = f
    img = np.zeros((h + 1, w + 1, 4), np.uint8)
    img[S & ~F, :3] = PAL[shade]; img[S & ~F, 3] = 255
    img[F, :3] = PAL[fill]; img[F, 3] = 255
    return img


def sweat():
    """汗珠：调色板里没有蓝色，特效层单独用浅蓝。"""
    rows = ["..#..", "..#..", ".###.", "##o##", "#####", ".###."]
    f = np.array([[c in "#o" for c in r] for r in rows]); h = np.array([[c == "o" for c in r] for r in rows])
    F = np.zeros((f.shape[0] + 2, f.shape[1] + 2), bool); F[1:-1, 1:-1] = f
    H = np.zeros_like(F); H[1:-1, 1:-1] = h
    o = F | np.roll(F, 1, 0) | np.roll(F, -1, 0) | np.roll(F, 1, 1) | np.roll(F, -1, 1)
    img = np.zeros((*F.shape, 4), np.uint8)
    img[o] = [*PAL[OUTLINE], 255]; img[F] = [150, 206, 238, 255]; img[H] = [236, 248, 255, 255]
    return img


CLOUD = ["....###..###....", "..#####.#####...", ".##############.", "################",
         "################", ".##############.", "..############..", "....###..###...."]


def cloud(phase):
    """小乌云，中间一条锯齿线，phase 不同则锯齿错开，两帧交替就像在翻滚。"""
    f = np.array([[c == "#" for c in r] for r in CLOUD])
    g = np.zeros_like(f); ys = [5, 4, 3, 4]
    for x in range(2, 14):
        g[ys[(x + phase) % 4], x] = True
    F = np.zeros((f.shape[0] + 2, f.shape[1] + 2), bool); F[1:-1, 1:-1] = f
    G = np.zeros_like(F); G[1:-1, 1:-1] = g
    o = F | np.roll(F, 1, 0) | np.roll(F, -1, 0) | np.roll(F, 1, 1) | np.roll(F, -1, 1)
    img = np.zeros((*F.shape, 4), np.uint8)
    img[o] = [*PAL[OUTLINE], 255]; img[F] = [*PAL[26], 255]
    img[F & ~np.roll(F, 1, 0)] = [*PAL[21], 255]
    img[G] = [*PAL[OUTLINE], 255]
    return img


def build_fx():
    fx = {
        "fx_bang": bang(BANG),
        "fx_bang_squash": bang([BANG[i] for i in (0, 1, 3, 5, 7, 9, 11, 12, 13)]),
        "fx_bang_stretch": bang(BANG[:1] + BANG[1:2] * 2 + BANG[1:]),
        "fx_spark": spark(),
        "fx_dot": shadowed(["###", "###", "###"]),
        "fx_star_big": outlined(["...#...", "...#...", "..###..", "###o###", "..###..", "...#...", "...#..."], HI, CREAM),
        "fx_star_small": outlined([".#.", "#o#", ".#."], HI, CREAM),
        "fx_z_big": shadowed(["#######", ".....#.", "....#..", "...#...", "..#....", ".#.....", "#######"]),
        "fx_z_small": shadowed(["#####", "...#.", "..#..", ".#...", "#####"]),
        "fx_sweat": sweat(),
        "fx_cloud_a": cloud(0),
        "fx_cloud_b": cloud(2),
    }
    for k, v in fx.items():
        Image.fromarray(v).save(OUT / f"{k}.png")


if __name__ == "__main__":
    print("生成主体图")
    build_bodies()
    print("生成补丁")
    build_patches()
    print("生成特效图")
    build_fx()
    print("完成，输出在", OUT)
