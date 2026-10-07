#!/usr/bin/env node
// Derives the tray/app icons (desktop/assets/) and the web favicons
// (web/public/) from the single logo master, web/src/assets/logo.png:
//   icon.png             1024x1024 app icon (white rounded tile, logo inset)
//   tray.png             32x32 tray icon with a background (Windows/Linux)
//   trayTemplate.png     16x16 macOS template image (black + alpha)
//   trayTemplate@2x.png  32x32 macOS template image (black + alpha)
//   favicon.png          256x256 browser tab icon
//   apple-touch-icon.png 180x180 iOS home-screen icon
// No dependencies: the PNG decoder, the area-average resampler and the
// encoder below are all local, matching the rest of this repo's tooling.
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { deflateSync, inflateSync } from 'node:zlib';

const scriptsDir = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(scriptsDir, '..', '..');
const appAssetsDir = path.resolve(scriptsDir, '..', 'assets');
const webPublicDir = path.resolve(repoRoot, 'web', 'public');
const logoPath = path.resolve(repoRoot, 'web', 'src', 'assets', 'logo.png');

// Pixels trimmed from the master: the exported illustration carries a few stray
// black pixels along the top/right/bottom edges that are not part of the mark.
const TRIM = 8;

// ---------- minimal PNG encoder (RGBA, 8-bit, filter 0) ----------
const CRC_TABLE = (() => {
  const t = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c;
  }
  return t;
})();

function crc32(buf) {
  let c = -1;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ -1) >>> 0;
}

function chunk(type, data) {
  const out = Buffer.alloc(12 + data.length);
  out.writeUInt32BE(data.length, 0);
  out.write(type, 4, 'ascii');
  data.copy(out, 8);
  out.writeUInt32BE(crc32(out.subarray(4, 8 + data.length)), 8 + data.length);
  return out;
}

function encodePng(width, height, rgba) {
  const stride = width * 4;
  const raw = Buffer.alloc((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    raw[y * (stride + 1)] = 0;
    rgba.copy(raw, y * (stride + 1) + 1, y * stride, (y + 1) * stride);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(width, 0);
  ihdr.writeUInt32BE(height, 4);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // color type RGBA
  return Buffer.concat([
    Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0)),
  ]);
}

// ---------- minimal PNG decoder (non-interlaced, 8-bit grey/RGB/RGBA) ----------
function decodePng(buf) {
  const signature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  if (!buf.subarray(0, 8).equals(signature)) throw new Error('not a PNG file');
  let pos = 8;
  let width = 0;
  let height = 0;
  let bitDepth = 0;
  let colorType = 0;
  const idat = [];
  while (pos + 8 <= buf.length) {
    const len = buf.readUInt32BE(pos);
    const type = buf.toString('ascii', pos + 4, pos + 8);
    const data = buf.subarray(pos + 8, pos + 8 + len);
    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
      if (data[12] !== 0) throw new Error('interlaced PNG is not supported');
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      break;
    }
    pos += 12 + len;
  }
  const channels = { 0: 1, 2: 3, 4: 2, 6: 4 }[colorType];
  if (bitDepth !== 8 || !channels) {
    throw new Error(`unsupported PNG: bit depth ${bitDepth}, color type ${colorType}`);
  }

  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const pixels = Buffer.alloc(stride * height);
  let prev = Buffer.alloc(stride);
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, y * (stride + 1) + 1 + stride));
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? line[i - channels] : 0;
      const b = prev[i];
      const c = i >= channels ? prev[i - channels] : 0;
      if (filter === 1) line[i] = (line[i] + a) & 0xff;
      else if (filter === 2) line[i] = (line[i] + b) & 0xff;
      else if (filter === 3) line[i] = (line[i] + ((a + b) >> 1)) & 0xff;
      else if (filter === 4) {
        const p = a + b - c;
        const pa = Math.abs(p - a);
        const pb = Math.abs(p - b);
        const pc = Math.abs(p - c);
        line[i] = (line[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c)) & 0xff;
      } else if (filter !== 0) throw new Error(`bad PNG filter ${filter}`);
    }
    line.copy(pixels, y * stride);
    prev = line;
  }

  const rgba = Buffer.alloc(width * height * 4);
  for (let i = 0; i < width * height; i++) {
    const s = i * channels;
    const o = i * 4;
    if (colorType === 0) {
      rgba[o] = rgba[o + 1] = rgba[o + 2] = pixels[s];
      rgba[o + 3] = 255;
    } else if (colorType === 4) {
      rgba[o] = rgba[o + 1] = rgba[o + 2] = pixels[s];
      rgba[o + 3] = pixels[s + 1];
    } else {
      rgba[o] = pixels[s];
      rgba[o + 1] = pixels[s + 1];
      rgba[o + 2] = pixels[s + 2];
      rgba[o + 3] = colorType === 6 ? pixels[s + 3] : 255;
    }
  }
  return { width, height, rgba };
}

// ---------- area-average resampler (alpha-weighted, no halos) ----------
function resample(src, sw, sh, dw, dh) {
  const out = Buffer.alloc(dw * dh * 4);
  for (let y = 0; y < dh; y++) {
    const y0 = Math.floor((y * sh) / dh);
    const y1 = Math.max(y0 + 1, Math.ceil(((y + 1) * sh) / dh));
    for (let x = 0; x < dw; x++) {
      const x0 = Math.floor((x * sw) / dw);
      const x1 = Math.max(x0 + 1, Math.ceil(((x + 1) * sw) / dw));
      let r = 0;
      let g = 0;
      let b = 0;
      let a = 0;
      let n = 0;
      for (let yy = y0; yy < y1 && yy < sh; yy++) {
        for (let xx = x0; xx < x1 && xx < sw; xx++) {
          const i = (yy * sw + xx) * 4;
          const alpha = src[i + 3] / 255;
          r += src[i] * alpha;
          g += src[i + 1] * alpha;
          b += src[i + 2] * alpha;
          a += src[i + 3];
          n++;
        }
      }
      const o = (y * dw + x) * 4;
      const cover = a / n / 255 || 1;
      out[o] = Math.round(r / n / cover);
      out[o + 1] = Math.round(g / n / cover);
      out[o + 2] = Math.round(b / n / cover);
      out[o + 3] = Math.round(a / n);
    }
  }
  return out;
}

// ---------- canvas helpers ----------
// White rounded tile with the art centred on it; 1px soft edge on the corner.
function drawTile(size, art, artSize, radius) {
  const rgba = Buffer.alloc(size * size * 4);
  const offset = Math.round((size - artSize) / 2);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let tileAlpha = 1;
      if (radius > 0) {
        const cx = Math.min(Math.max(x + 0.5, radius), size - radius);
        const cy = Math.min(Math.max(y + 0.5, radius), size - radius);
        tileAlpha = Math.min(1, Math.max(0, radius + 0.5 - Math.hypot(x + 0.5 - cx, y + 0.5 - cy)));
      }
      const o = (y * size + x) * 4;
      rgba[o] = rgba[o + 1] = rgba[o + 2] = 255;
      rgba[o + 3] = Math.round(255 * tileAlpha);
      const ax = Math.floor(((x - offset) * art.w) / artSize);
      const ay = Math.floor(((y - offset) * art.h) / artSize);
      if (ax < 0 || ay < 0 || ax >= art.w || ay >= art.h) continue;
      const s = (ay * art.w + ax) * 4;
      const sa = (art.rgba[s + 3] / 255) * tileAlpha;
      if (sa <= 0) continue;
      for (let k = 0; k < 3; k++) rgba[o + k] = Math.round(art.rgba[s + k] * sa + 255 * (1 - sa));
      rgba[o + 3] = Math.round(255 * (tileAlpha + sa * (1 - tileAlpha)));
    }
  }
  return rgba;
}

// macOS template image: the mark as a black glyph whose alpha is the ink
// coverage, so the system can tint it for light and dark menu bars.
// `curve` = [threshold, gain] boosts midtones: a detailed illustration
// averaged down to 16px otherwise turns into a low-alpha grey haze.
function drawInk(size, art, artSize, curve = [0, 1]) {
  const [threshold, gain] = curve;
  const rgba = Buffer.alloc(size * size * 4);
  const offset = Math.round((size - artSize) / 2);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      const ax = Math.floor(((x - offset) * art.w) / artSize);
      const ay = Math.floor(((y - offset) * art.h) / artSize);
      if (ax < 0 || ay < 0 || ax >= art.w || ay >= art.h) continue;
      const s = (ay * art.w + ax) * 4;
      const lum = 0.2126 * art.rgba[s] + 0.7152 * art.rgba[s + 1] + 0.0722 * art.rgba[s + 2];
      const ink = (1 - lum / 255) * (art.rgba[s + 3] / 255);
      const shaped = Math.min(1, Math.max(0, (ink - threshold) * gain));
      const o = (y * size + x) * 4;
      rgba[o + 3] = Math.round(shaped * 255);
    }
  }
  return rgba;
}

// ---------- pipeline ----------
const master = decodePng(readFileSync(logoPath));
const cropW = master.width - TRIM * 2;
const cropH = master.height - TRIM * 2;
if (cropW <= 0 || cropH <= 0) throw new Error('logo.png is smaller than the trim border');
const cropped = Buffer.alloc(cropW * cropH * 4);
for (let y = 0; y < cropH; y++) {
  const from = ((y + TRIM) * master.width + TRIM) * 4;
  master.rgba.copy(cropped, y * cropW * 4, from, from + cropW * 4);
}
// Square the master up on white so every derived size keeps the art centred.
const side = Math.max(cropW, cropH);
const square = Buffer.alloc(side * side * 4, 255);
for (let y = 0; y < cropH; y++) {
  const to = ((y + ((side - cropH) >> 1)) * side + ((side - cropW) >> 1)) * 4;
  cropped.copy(square, to, y * cropW * 4, (y + 1) * cropW * 4);
}
const art = (size) => ({ w: size, h: size, rgba: resample(square, side, side, size, size) });

mkdirSync(appAssetsDir, { recursive: true });

// App icon: white rounded tile, art inset 4%.
{
  const size = 1024;
  const artSize = Math.round(size * 0.92);
  writeFileSync(
    path.join(appAssetsDir, 'icon.png'),
    encodePng(size, size, drawTile(size, art(artSize), artSize, Math.round(size * 0.21))),
  );
  console.log('[gen-icons] wrote icon.png (1024x1024)');
}

// Tray (Windows/Linux): small rounded tile so the mark reads on any tray theme.
{
  const size = 32;
  const artSize = Math.round(size * 0.94);
  writeFileSync(
    path.join(appAssetsDir, 'tray.png'),
    encodePng(size, size, drawTile(size, art(artSize), artSize, Math.round(size * 0.18))),
  );
  console.log('[gen-icons] wrote tray.png (32x32)');
}

// macOS template images: black glyph + alpha only. 16px gets a stronger
// contrast curve than 32px, because that is where lines start to average away.
for (const [name, size, curve] of [
  ['trayTemplate.png', 16, [0.15, 2.2]],
  ['trayTemplate@2x.png', 32, [0.05, 1.4]],
]) {
  const artSize = Math.round(size * 0.96);
  writeFileSync(
    path.join(appAssetsDir, name),
    encodePng(size, size, drawInk(size, art(artSize), artSize, curve)),
  );
  console.log(`[gen-icons] wrote ${name} (${size}x${size})`);
}

// Web favicons share the same master so the tab icon never drifts from the app.
mkdirSync(webPublicDir, { recursive: true });
for (const [name, size, radius, inset] of [
  ['favicon.png', 256, 0.18, 0.06],
  ['apple-touch-icon.png', 180, 0, 0.08],
]) {
  const artSize = Math.round(size * (1 - inset * 2));
  writeFileSync(
    path.join(webPublicDir, name),
    encodePng(size, size, drawTile(size, art(artSize), artSize, Math.round(size * radius))),
  );
  console.log(`[gen-icons] wrote ${name} (${size}x${size})`);
}
