/**
 * 桌宠素材构建 —— 从原图一步产出干净的角色贴图。
 *
 * 输入: pet/source-original.webp   （黑底 + 左上角「AI生成」水印）
 * 输出: pet/phoebe.png            （256 宽，透明底，已裁掉空白）
 *       pet/phoebe@2x.png         （512 宽，高清屏用）
 *
 * 三步：
 *   1) 去水印
 *   2) 去黑底（连通域法，见下方说明）
 *   3) 按 alpha 包围盒裁剪 + 缩放
 *
 * ── 为什么不用「亮度阈值」 ──────────────────────────────
 * 原图背景是 0~30 的渐变，而角色描边也在 1~15，两者在颜色上重叠。
 * 任何「亮度低于 X 就透明」都只能二选一：要么留黑底，要么连描边一起吃掉。
 *
 * ── 为什么不用 minimax 泛洪 ────────────────────────────
 * 试过一版「从边界算 routeMax = 路径上最亮像素的最小值」，背景内部 routeMax
 * 很小、被描边围住的角色很大，看似能分开。但它会吃掉**白色帽檐**：帽檐外侧是
 * 白色（亮度≈240），从黑背景走到它外侧那圈浅色反锯齿带上 routeMax 还没超阈值，
 * 于是整块白帽檐被当作背景清掉（用户反馈的「帽子缺了一个角」正是这个）。
 * 调高阈值吃更多白，调低阈值留黑底，无解。
 *
 * ── 现在的做法：连通域 ────────────────────────────────
 *   1) 标出「暗像素」（亮度 < DARK）
 *   2) 只保留与画布四边连通的那个暗色连通域 → 这才是背景
 *   3) 只有背景像素变透明；其余像素原样保留，所以白帽檐、白描边都不受影响
 *   4) 用 4 邻接而非 8 邻接：斜向能穿过 1px 宽的反锯齿描边，是泄漏通道
 *   5) mask 模糊 2px 得到平滑过渡带，不做硬切，边缘不留锯齿
 *
 * 用法: node build-pet-asset.mjs
 *   TAU 类环境变量已不需要；想调试可用 OUT_SUFFIX 改输出名。
 */
import { mkdirSync, existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Resolve "sharp" from a user-supplied module directory.
//
// Set SHARP_MODULES to a folder containing node_modules/sharp, e.g.
//     SHARP_MODULES=C:/path/to/profiles/node_modules/
// It falls back to the normal Node resolution (i.e. a local node_modules)
// when the variable is unset, so a plain `npm i sharp` also works.
const require = createRequire(
  process.env.SHARP_MODULES
    ? process.env.SHARP_MODULES.replace(/\/?$/, '/')
    : import.meta.url
);
const sharp = require('sharp');

// Input image lives next to this script; override with DIR if needed.
const DIR = process.env.DIR ?? path.dirname(fileURLToPath(import.meta.url));
const IN = path.join(DIR, 'source-original.webp');
const OUT_SUFFIX = process.env.OUT_SUFFIX ?? '';

const WM = { x0: 0, y0: 0, x1: 375, y1: 145 };   // 水印安全区
const DARK = Number(process.env.DARK ?? 60);     // 低于此亮度才「可能是背景」
const FEATHER = 2;                               // 边缘过渡带宽度（像素）
const WORK_W = Number(process.env.WORK_W ?? 1400);

if (!existsSync(IN)) {
  console.error(`缺少输入文件: ${IN}`);
  process.exit(1);
}
mkdirSync(DIR, { recursive: true });

// ---------- 读取 ----------
const srcMeta = await sharp(IN, { limitInputPixels: false }).metadata();
const { data, info } = await sharp(IN, { limitInputPixels: false })
  .resize({ width: WORK_W, kernel: 'lanczos3', fit: 'inside' })
  .ensureAlpha()
  .raw()
  .toBuffer({ resolveWithObject: true });
const { width: W, height: H } = info;
const N = W * H;
console.log(`原图 ${srcMeta.width}x${srcMeta.height} → 处理尺寸 ${W}x${H}`);

// ---------- 1) 去水印 ----------
let wmCleared = 0;
for (let y = WM.y0; y < Math.min(WM.y1, H); y++) {
  for (let x = WM.x0; x < Math.min(WM.x1, W); x++) {
    const i = (y * W + x) * 4;
    if (data[i + 3] > 0) { data[i + 3] = 0; wmCleared++; }
  }
}

// ---------- 2) 连通域法去黑底 ----------
const lum = new Uint8Array(N);
for (let p = 0, i = 0; p < N; p++, i += 4) {
  lum[p] = (0.299 * data[i] + 0.587 * data[i + 1] + 0.114 * data[i + 2]) | 0;
}

const isBg = new Uint8Array(N);
const queue = new Int32Array(N);
let head = 0;
let tail = 0;
const push = (p) => {
  if (isBg[p] === 0 && lum[p] < DARK) { isBg[p] = 1; queue[tail++] = p; }
};

// 种子：四条边
for (let x = 0; x < W; x++) { push(x); push((H - 1) * W + x); }
for (let y = 0; y < H; y++) { push(y * W); push(y * W + W - 1); }

// 4 邻接扩散
while (head < tail) {
  const p = queue[head++];
  const x = p % W;
  const y = (p - x) / W;
  if (x > 0) push(p - 1);
  if (x < W - 1) push(p + 1);
  if (y > 0) push(p - W);
  if (y < H - 1) push(p + W);
}

const mask = Buffer.alloc(N);
let bgCount = 0;
for (let p = 0; p < N; p++) {
  if (isBg[p]) { mask[p] = 0; bgCount++; } else mask[p] = 255;
}
console.log(`背景连通域 ${bgCount}px（${((bgCount / N) * 100).toFixed(1)}%）`);

/**
 * 对单通道 mask 做两次盒式模糊（近似高斯），返回 0..255。
 *
 * 为什么不用 sharp 的 .blur()：
 *   sharp 读/写 raw 像素时会强制按 3 通道（RGB）处理，不认 channels:1。
 *   把单通道数据丢进去，输出长度会变成 3 倍、每 3 个像素才有一个真值，
 *   画面就会出现横条纹（这个坑实际踩过）。自己算 40 行更省心也更快。
 * 用积分图实现，整体 O(N)。
 */
function blurMask(src, W, H, radius) {
  const tmp = new Uint8Array(W * H);
  const out = new Uint8Array(W * H);
  const win = radius * 2 + 1;

  // 横向
  for (let y = 0; y < H; y++) {
    const row = y * W;
    let sum = 0;
    for (let x = -radius; x <= radius; x++) sum += src[row + Math.min(W - 1, Math.max(0, x))];
    for (let x = 0; x < W; x++) {
      tmp[row + x] = Math.round(sum / win);
      const addX = Math.min(W - 1, x + radius + 1);
      const subX = Math.max(0, x - radius);
      sum += src[row + addX] - src[row + subX];
    }
  }

  // 纵向
  for (let x = 0; x < W; x++) {
    let sum = 0;
    for (let y = -radius; y <= radius; y++) sum += tmp[Math.min(H - 1, Math.max(0, y)) * W + x];
    for (let y = 0; y < H; y++) {
      out[y * W + x] = Math.round(sum / win);
      const addY = Math.min(H - 1, y + radius + 1);
      const subY = Math.max(0, y - radius);
      sum += tmp[addY * W + x] - tmp[subY * W + x];
    }
  }
  return out;
}

// 模糊出过渡带：mask 内部=255、背景=0，模糊后交界处自然形成 0→255 斜坡
const soft = blurMask(mask, W, H, FEATHER);
if (soft.length !== N) throw new Error(`mask 尺寸异常: ${soft.length} != ${N}`);

let transparent = 0;
let softened = 0;
for (let p = 0, i = 0; p < N; p++, i += 4) {
  const a = Math.round((data[i + 3] * soft[p]) / 255);
  data[i + 3] = a;
  if (a === 0) { transparent++; continue; }
  if (a < 255) {
    softened++;
    // 半透明边缘原本混了黑底，反预乘避免浅色背景上显黑边
    const f = a / 255;
    data[i] = Math.min(255, Math.round(data[i] / f));
    data[i + 1] = Math.min(255, Math.round(data[i + 1] / f));
    data[i + 2] = Math.min(255, Math.round(data[i + 2] / f));
  }
}
console.log(`水印清除 ${wmCleared}px，完全透明 ${((transparent / N) * 100).toFixed(1)}%，过渡带 ${softened}px`);

const cut = await sharp(data, { raw: { width: W, height: H, channels: 4 } }).png().toBuffer();

// ---------- 3) 按「最大前景连通域」求包围盒 ----------
// 不能只用「有的不透明像素」或「整行有几个不透明像素」来定边界：
// 角落残留的几个孤立像素（不在背景连通域里，于是被判成前景）会把框撑满整幅。
// 改成对前景再做一次连通域分析，取最大的那一块 —— 那就是角色本体。
function largestForegroundBox(alpha, W, H, alphaMin) {
  const seen = new Uint8Array(W * H);
  const stack = new Int32Array(W * H);
  let best = { size: 0, box: null };

  for (let s = 0; s < W * H; s++) {
    if (seen[s] || alpha[s] < alphaMin) continue;
    let sp = 0;
    stack[sp++] = s;
    seen[s] = 1;
    let size = 0;
    let x0 = W;
    let y0 = H;
    let x1 = -1;
    let y1 = -1;

    while (sp > 0) {
      const p = stack[--sp];
      const x = p % W;
      const y = (p - x) / W;
      size++;
      if (x < x0) x0 = x;
      if (x > x1) x1 = x;
      if (y < y0) y0 = y;
      if (y > y1) y1 = y;
      if (x > 0) { const q = p - 1; if (!seen[q] && alpha[q] >= alphaMin) { seen[q] = 1; stack[sp++] = q; } }
      if (x < W - 1) { const q = p + 1; if (!seen[q] && alpha[q] >= alphaMin) { seen[q] = 1; stack[sp++] = q; } }
      if (y > 0) { const q = p - W; if (!seen[q] && alpha[q] >= alphaMin) { seen[q] = 1; stack[sp++] = q; } }
      if (y < H - 1) { const q = p + W; if (!seen[q] && alpha[q] >= alphaMin) { seen[q] = 1; stack[sp++] = q; } }
    }
    if (size > best.size) best = { size, box: { x0, y0, x1, y1 } };
  }
  return best;
}

const alphaChan = new Uint8Array(N);
for (let p = 0, i = 3; p < N; p++, i += 4) alphaChan[p] = data[i];

const fg = largestForegroundBox(alphaChan, W, H, 128);
if (!fg.box) throw new Error('没找到前景，可能阈值不合适');
console.log(`最大前景连通域 ${fg.size}px，包围盒 (${fg.box.x0},${fg.box.y0})-(${fg.box.x1},${fg.box.y1})`);

const PAD = 4;
const box = {
  left: Math.max(0, fg.box.x0 - PAD),
  top: Math.max(0, fg.box.y0 - PAD),
  width: Math.min(W - 1, fg.box.x1 + PAD) - Math.max(0, fg.box.x0 - PAD) + 1,
  height: Math.min(H - 1, fg.box.y1 + PAD) - Math.max(0, fg.box.y0 - PAD) + 1,
};
console.log(`裁剪框 ${box.width}x${box.height} @ (${box.left},${box.top})  ` +
  `（占处理尺寸 ${((box.width / W) * 100).toFixed(0)}% x ${((box.height / H) * 100).toFixed(0)}%）`);

const outputs = [];
for (const [name, width] of [[`phoebe${OUT_SUFFIX}.png`, 256], [`phoebe${OUT_SUFFIX}@2x.png`, 512]]) {
  const out = `${DIR}/${name}`;
  await sharp(cut).extract(box).resize({ width, kernel: 'lanczos3', fit: 'inside' })
    .png({ compressionLevel: 9 }).toFile(out);
  const m = await sharp(out).metadata();
  outputs.push({ file: name, size: `${m.width}x${m.height}` });
  console.log(`  ✓ ${name}  ${m.width}x${m.height}`);
}

console.log(JSON.stringify({ source: IN, workSize: `${W}x${H}`, dark: DARK, box, outputs }, null, 2));
