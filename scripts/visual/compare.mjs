#!/usr/bin/env node
// Compares the web capture with the Avalonia capture scene by scene (plan avalonia-client §23). No dependencies:
// PNG decode/encode is done with node:zlib.
//
//   node scripts/visual/compare.mjs [--web artifacts/visual/web] [--avalonia artifacts/visual/avalonia]
//        [--out artifacts/visual/report] [--thresholds scripts/visual/thresholds.json] [--only a,b] [--strict] [--no-fail]
//
// Per scene (<page>__<state>__<theme>):
//   mad         mean absolute difference over all pixels and RGB channels, 0..1
//   pctOver     % of pixels whose largest channel difference is above `pixelDelta` (default 24/255)
//   grid        mean of the 16x16 cell differences (a coarse layout / color-block comparison: it survives font differences)
//   gridMasked  the same without text-heavy cells (cells dense in edges on BOTH sides are text, whose glyph shapes differ by
//               font rasterizer); `maskedCells` says how many were ignored
//   cellsOver   cells whose mean difference is above `cellDelta` (default 0.12): where the layout really differs
// Output: <out>/report.json, <out>/<scene>.png (web | avalonia | diff) and a table on stdout. A scene present on one side
// only is listed as web-only / avalonia-only (a warning; --strict or thresholds.json "missing":"fail" fails on it).
// Exit code 1 when a scene is over its thresholds (--no-fail: always 0).
import { readFileSync, writeFileSync, readdirSync, mkdirSync, existsSync } from 'node:fs';
import { join, dirname, resolve, basename } from 'node:path';
import { fileURLToPath } from 'node:url';
import zlib from 'node:zlib';

const repo = dirname(dirname(dirname(fileURLToPath(import.meta.url))));
const argv = process.argv.slice(2);
const opt = (n, d) => { const i = argv.indexOf(`--${n}`); return i >= 0 ? argv[i + 1] : d; };
const has = (n) => argv.includes(`--${n}`);
const webDir = resolve(repo, opt('web', 'artifacts/visual/web'));
const avaDir = resolve(repo, opt('avalonia', 'artifacts/visual/avalonia'));
const outDir = resolve(repo, opt('out', 'artifacts/visual/report'));
const thresholdsPath = resolve(repo, opt('thresholds', 'scripts/visual/thresholds.json'));
const only = (opt('only', '') ?? '').split(',').filter(Boolean);
const config = existsSync(thresholdsPath) ? JSON.parse(readFileSync(thresholdsPath, 'utf8')) : {};
const defaults = { pixelDelta: 24, cellDelta: 0.12, textEdgeDensity: 0.08, ...(config.default ?? {}) };
const GRID = 16;

// ------------------------------------------------------------------ PNG
const crcTable = new Uint32Array(256).map((_, n) => { let c = n; for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1; return c >>> 0; });
const crc32 = (buf) => { let c = 0xffffffff; for (const b of buf) c = crcTable[(c ^ b) & 255] ^ (c >>> 8); return (c ^ 0xffffffff) >>> 0; };

/** → { width, height, data: Uint8Array RGBA }. Non-interlaced, any color type, 8 or 16 bit. */
function decodePng(buf) {
  if (buf.readUInt32BE(0) !== 0x89504e47) throw new Error('not a PNG');
  let pos = 8, w = 0, h = 0, depth = 8, ctype = 6, interlace = 0, plte = null, trns = null;
  const idat = [];
  while (pos < buf.length) {
    const len = buf.readUInt32BE(pos), type = buf.toString('latin1', pos + 4, pos + 8), body = buf.subarray(pos + 8, pos + 8 + len);
    pos += 12 + len;
    if (type === 'IHDR') { w = body.readUInt32BE(0); h = body.readUInt32BE(4); depth = body[8]; ctype = body[9]; interlace = body[12]; }
    else if (type === 'PLTE') plte = body;
    else if (type === 'tRNS') trns = body;
    else if (type === 'IDAT') idat.push(body);
    else if (type === 'IEND') break;
  }
  if (interlace) throw new Error('interlaced PNG not supported');
  const channels = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[ctype];
  const bpp = Math.max(1, (channels * depth) >> 3);
  const stride = (w * channels * depth + 7) >> 3;
  const raw = zlib.inflateSync(Buffer.concat(idat));
  const out = new Uint8Array(h * stride);
  for (let y = 0; y < h; y++) {
    const f = raw[y * (stride + 1)], src = y * (stride + 1) + 1, dst = y * stride;
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? out[dst + x - bpp] : 0, b = y ? out[dst - stride + x] : 0, c = x >= bpp && y ? out[dst - stride + x - bpp] : 0;
      let v = raw[src + x];
      if (f === 1) v += a; else if (f === 2) v += b; else if (f === 3) v += (a + b) >> 1;
      else if (f === 4) { const p = a + b - c, pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c); v += pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
      out[dst + x] = v & 255;
    }
  }
  const rgba = new Uint8Array(w * h * 4);
  const sample = (row, i) => depth === 16 ? out[row + i * 2] : depth === 8 ? out[row + i] : (out[row + ((i * depth) >> 3)] >> (8 - depth - ((i * depth) & 7))) & ((1 << depth) - 1);
  for (let y = 0; y < h; y++) {
    const row = y * stride;
    for (let x = 0; x < w; x++) {
      const o = (y * w + x) * 4;
      if (ctype === 6) { for (let k = 0; k < 4; k++) rgba[o + k] = sample(row, x * 4 + k); }
      else if (ctype === 2) { for (let k = 0; k < 3; k++) rgba[o + k] = sample(row, x * 3 + k); rgba[o + 3] = 255; }
      else if (ctype === 0) { const s = sample(row, x), v = depth < 8 ? Math.round((s * 255) / ((1 << depth) - 1)) : s; rgba[o] = rgba[o + 1] = rgba[o + 2] = v; rgba[o + 3] = 255; }
      else if (ctype === 4) { rgba[o] = rgba[o + 1] = rgba[o + 2] = sample(row, x * 2); rgba[o + 3] = sample(row, x * 2 + 1); }
      else { const i = sample(row, x); rgba[o] = plte[i * 3]; rgba[o + 1] = plte[i * 3 + 1]; rgba[o + 2] = plte[i * 3 + 2]; rgba[o + 3] = trns && i < trns.length ? trns[i] : 255; }
    }
  }
  return { width: w, height: h, data: rgba };
}

function encodePng({ width, height, data }) {
  const chunk = (type, body) => {
    const b = Buffer.alloc(12 + body.length);
    b.writeUInt32BE(body.length, 0); b.write(type, 4, 'latin1'); Buffer.from(body).copy(b, 8);
    b.writeUInt32BE(crc32(b.subarray(4, 8 + body.length)), 8 + body.length);
    return b;
  };
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0); ihdr.writeUInt32BE(height, 4); ihdr[8] = 8; ihdr[9] = 6;
  const raw = Buffer.alloc(height * (width * 4 + 1));
  for (let y = 0; y < height; y++) { raw[y * (width * 4 + 1)] = 0; Buffer.from(data.buffer, data.byteOffset + y * width * 4, width * 4).copy(raw, y * (width * 4 + 1) + 1); }
  return Buffer.concat([Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', ihdr), chunk('IDAT', zlib.deflateSync(raw, { level: 6 })), chunk('IEND', Buffer.alloc(0))]);
}

/** Bilinear resize to (w, h), used when the two captures were taken at different scales. */
function resize(img, w, h) {
  if (img.width === w && img.height === h) return img;
  const out = new Uint8Array(w * h * 4);
  for (let y = 0; y < h; y++) {
    const fy = ((y + 0.5) * img.height) / h - 0.5, y0 = Math.max(0, Math.floor(fy)), y1 = Math.min(img.height - 1, y0 + 1), ty = Math.max(0, fy - y0);
    for (let x = 0; x < w; x++) {
      const fx = ((x + 0.5) * img.width) / w - 0.5, x0 = Math.max(0, Math.floor(fx)), x1 = Math.min(img.width - 1, x0 + 1), tx = Math.max(0, fx - x0);
      for (let k = 0; k < 4; k++) {
        const p = (yy, xx) => img.data[(yy * img.width + xx) * 4 + k];
        out[(y * w + x) * 4 + k] = Math.round((p(y0, x0) * (1 - tx) + p(y0, x1) * tx) * (1 - ty) + (p(y1, x0) * (1 - tx) + p(y1, x1) * tx) * ty);
      }
    }
  }
  return { width: w, height: h, data: out };
}

// ------------------------------------------------------------------ metrics
/** Per-pixel "edge" flag: strong horizontal luminance step to the right neighbour (what glyph strokes produce). */
function edgeMap(img) {
  const { width: w, height: h, data: d } = img;
  const lum = (i) => 0.299 * d[i] + 0.587 * d[i + 1] + 0.114 * d[i + 2];
  const e = new Uint8Array(w * h);
  for (let y = 0; y < h; y++) for (let x = 0; x < w - 1; x++) { const i = (y * w + x) * 4; if (Math.abs(lum(i) - lum(i + 4)) > 40) e[y * w + x] = 1; }
  return e;
}

function compareImages(a, b, t) {
  const w = a.width, h = a.height;
  const sizeNote = b.width !== w || b.height !== h ? `avalonia ${b.width}x${b.height} resampled to web ${w}x${h}` : null;
  if (sizeNote) b = resize(b, w, h);
  const A = a.data, B = b.data;
  const cellSum = new Float64Array(GRID * GRID), cellN = new Uint32Array(GRID * GRID), cellEdgeA = new Uint32Array(GRID * GRID), cellEdgeB = new Uint32Array(GRID * GRID);
  const ea = edgeMap(a), eb = edgeMap(b);
  const diff = new Uint8Array(w * h * 4);
  let sum = 0, over = 0;
  for (let y = 0; y < h; y++) {
    const cy = Math.min(GRID - 1, Math.floor((y * GRID) / h));
    for (let x = 0; x < w; x++) {
      const i = (y * w + x) * 4, c = cy * GRID + Math.min(GRID - 1, Math.floor((x * GRID) / w));
      const dr = Math.abs(A[i] - B[i]), dg = Math.abs(A[i + 1] - B[i + 1]), db = Math.abs(A[i + 2] - B[i + 2]);
      const m = (dr + dg + db) / 3;
      sum += m; cellSum[c] += m; cellN[c]++;
      if (Math.max(dr, dg, db) > t.pixelDelta) over++;
      cellEdgeA[c] += ea[y * w + x]; cellEdgeB[c] += eb[y * w + x];
      const v = Math.min(255, Math.max(dr, dg, db) * 2);
      diff[i] = 255; diff[i + 1] = 255 - v; diff[i + 2] = 255 - v; diff[i + 3] = 255;
    }
  }
  let gridSum = 0, maskedSum = 0, masked = 0, cellsOver = 0;
  const cells = [];
  for (let c = 0; c < GRID * GRID; c++) {
    const d = cellSum[c] / cellN[c] / 255;
    const text = cellEdgeA[c] / cellN[c] > t.textEdgeDensity && cellEdgeB[c] / cellN[c] > t.textEdgeDensity;
    gridSum += d;
    if (text) masked++; else maskedSum += d;
    if (d > t.cellDelta) cellsOver++;
    cells.push({ d: +d.toFixed(4), text });
  }
  const r = (n) => Math.round(n * 10000) / 10000;
  return {
    metrics: { mad: r(sum / (w * h) / 255), pctOver: Math.round((over / (w * h)) * 10000) / 100, grid: r(gridSum / (GRID * GRID)), gridMasked: r(masked === GRID * GRID ? 0 : maskedSum / (GRID * GRID - masked)), maskedCells: masked, cellsOver },
    cells, sizeNote, diff: { width: w, height: h, data: diff }, b,
  };
}

function sideBySide(a, b, d) {
  const gap = 8, w = a.width * 3 + gap * 2, h = a.height;
  const out = new Uint8Array(w * h * 4).fill(255);
  [a, b, d].forEach((img, k) => {
    for (let y = 0; y < h; y++) out.set(img.data.subarray(y * img.width * 4, (y + 1) * img.width * 4), (y * w + k * (a.width + gap)) * 4);
  });
  return { width: w, height: h, data: out };
}

// ------------------------------------------------------------------ main
const scenesIn = (dir) => existsSync(dir) ? readdirSync(dir).filter((f) => f.endsWith('.png')).map((f) => basename(f, '.png')) : [];
const webScenes = new Set(scenesIn(webDir)), avaScenes = new Set(scenesIn(avaDir));
const names = [...new Set([...webScenes, ...avaScenes])].sort().filter((n) => !only.length || only.some((o) => n === o || n.startsWith(`${o}__`)));
mkdirSync(outDir, { recursive: true });

const results = [];
for (const name of names) {
  const inWeb = webScenes.has(name), inAva = avaScenes.has(name);
  if (!(inWeb && inAva)) { results.push({ scene: name, status: inWeb ? 'web-only' : 'avalonia-only' }); continue; }
  const th = { ...defaults, ...(config.scenes?.[name] ?? {}) };
  const a = decodePng(readFileSync(join(webDir, `${name}.png`))), b0 = decodePng(readFileSync(join(avaDir, `${name}.png`)));
  const { metrics, cells, sizeNote, diff, b } = compareImages(a, b0, th);
  const limits = ['mad', 'pctOver', 'grid', 'gridMasked', 'cellsOver'].filter((k) => th[k] !== undefined);
  const failed = limits.filter((k) => metrics[k] > th[k]);
  writeFileSync(join(outDir, `${name}.png`), encodePng(sideBySide(a, b, diff)));
  results.push({ scene: name, status: failed.length ? 'fail' : 'pass', failed, metrics, thresholds: Object.fromEntries(limits.map((k) => [k, th[k]])), sizeNote, cells });
}

const missingFails = has('strict') || config.missing === 'fail';
const report = {
  generatedAt: new Date().toISOString(), webDir, avaloniaDir: avaDir, grid: GRID,
  summary: {
    compared: results.filter((r) => r.metrics).length, pass: results.filter((r) => r.status === 'pass').length, fail: results.filter((r) => r.status === 'fail').length,
    webOnly: results.filter((r) => r.status === 'web-only').map((r) => r.scene), avaloniaOnly: results.filter((r) => r.status === 'avalonia-only').map((r) => r.scene),
  },
  results,
};
writeFileSync(join(outDir, 'report.json'), JSON.stringify(report, null, 2));

const pad = (s, n) => String(s).padEnd(n);
console.log(`${pad('scene', 34)}${pad('status', 8)}${pad('mad', 8)}${pad('pctOver', 9)}${pad('grid', 8)}${pad('gridMask', 10)}${pad('cellsOver', 10)}over-limit`);
for (const r of results) {
  if (!r.metrics) { console.log(`${pad(r.scene, 34)}${r.status.toUpperCase()}   (${r.status === 'web-only' ? 'no Avalonia scene yet' : 'no web capture'})`); continue; }
  const m = r.metrics;
  console.log(`${pad(r.scene, 34)}${pad(r.status.toUpperCase(), 8)}${pad(m.mad, 8)}${pad(m.pctOver, 9)}${pad(m.grid, 8)}${pad(m.gridMasked, 10)}${pad(m.cellsOver, 10)}${r.failed.join(',')}`);
}
const s = report.summary;
console.log(`\ncompared ${s.compared}: ${s.pass} pass, ${s.fail} fail; web-only ${s.webOnly.length}, avalonia-only ${s.avaloniaOnly.length}`);
if (s.avaloniaOnly.length) console.log(`avalonia-only (no web capture; add it to capture-web.mjs): ${s.avaloniaOnly.join(', ')}`);
console.log(`report: ${join(outDir, 'report.json')}  (side-by-side PNGs: web | avalonia | diff)`);
const bad = s.fail > 0 || (missingFails && (s.webOnly.length || s.avaloniaOnly.length));
process.exit(bad && !has('no-fail') ? 1 : 0);
