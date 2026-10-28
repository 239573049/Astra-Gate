#!/usr/bin/env node
// Generates the brand icon data of the Avalonia client's ui kit (plan avalonia-client §21 WP0b, GAP-1):
//   src/Astra.Desktop/Components/Kit/Brands/BrandMarkData.g.cs  - vector marks (path data) of @lobehub/icons
//   src/Astra.Desktop/Components/Kit/Brands/BrandLogoData.g.cs  - the vendor logos web/src/assets ships as images
// from the same sources web/src/components/icons.tsx draws, so the two clients show the same marks.
// Marks: the web's @lobehub/icons React components are rendered to SVG and flattened to path layers (paths, circles,
// ellipses, linear gradients - the only features those icons use). Logos: png / svg files resized to 128 px PNGs
// (sharp, found in the pnpm store) and embedded as base64, so no csproj resource entry is needed.
// `--check` exits 1 when the checked-in files differ. Uses the repo's esbuild; adds no dependency.
import { existsSync, mkdirSync, readFileSync, readdirSync, writeFileSync, rmSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const outDir = join(root, 'src/Astra.Desktop/Components/Kit/Brands');
const check = process.argv.includes('--check');
const lobe = join(root, 'web/node_modules/@lobehub/icons/es');

// Every @lobehub/icons component web/src/components/icons.tsx imports.
const names = (
  'Anthropic DeepSeek Gemini LmStudio Minimax Moonshot Ollama OpenAI OpenRouter Qoder Qwen SiliconCloud Volcengine XAI Zhipu ' +
  'Claude GithubCopilot Grok HermesAgent Kimi Meta OpenCode Pi XiaomiMiMo ZAI ' +
  'Baichuan ChatGLM Cohere Doubao Gemma Hunyuan LongCat Mistral Stepfun Wenxin Yi'
).split(' ');

// Vendor logos web/src/components/icons.tsx imports as files (clients and providers have their own copies of some).
const LOGOS = {
  crush: 'web/src/assets/clients/crush.png',
  droid: 'web/src/assets/clients/droid.svg',
  zed: 'web/src/assets/clients/zed.png',
  'vscode-insiders': 'web/src/assets/clients/vscode-insiders.png',
  vscodium: 'web/src/assets/clients/vscodium.svg',
  omp: 'web/src/assets/clients/omp.png',
  workbuddy: 'web/src/assets/clients/workbuddy.svg',
  'nextcowork-client': 'web/src/assets/clients/nextcowork.png',
  paseo: 'web/src/assets/clients/paseo.png',
  tkcode: 'web/src/assets/clients/tkcode.png',
  'nextcowork-provider': 'web/src/assets/providers/nextcowork.png',
  routin: 'web/src/assets/providers/routin.png',
};

function findPackage(name, pnpmPrefix) {
  for (const base of ['web', 'cli', '.']) {
    try {
      return createRequire(join(root, base, 'package.json'))(name);
    } catch {
      /* next */
    }
  }
  const store = join(root, 'node_modules/.pnpm');
  const dirs = existsSync(store) ? readdirSync(store).filter((d) => d.startsWith(pnpmPrefix)).sort().reverse() : [];
  for (const d of dirs) {
    const p = join(store, d, 'node_modules', name);
    if (existsSync(p)) return createRequire(join(p, 'package.json'))(p);
  }
  throw new Error(`${name} not found; run \`pnpm install\` first`);
}

// ---------- marks ----------

async function renderSvgs() {
  const esbuild = findPackage('esbuild', 'esbuild@');
  let src = `import {renderToStaticMarkup} from 'react-dom/server';import {createElement} from 'react';const out={};\n`;
  for (const n of names) {
    for (const v of ['Mono', 'Color']) {
      if (existsSync(`${lobe}/${n}/components/${v}.js`)) {
        src += `import ${n}${v} from '${lobe}/${n}/components/${v}.js';out['${n}.${v}']=renderToStaticMarkup(createElement(${n}${v}));\n`;
      }
    }
    src += `import * as S${n} from '${lobe}/${n}/style.js';out['${n}.style']={...S${n}};\n`;
  }
  src += `console.log(JSON.stringify(out));`;
  const built = await esbuild.build({
    stdin: { contents: src, resolveDir: join(root, 'web'), loader: 'js' },
    bundle: true,
    format: 'cjs',
    platform: 'node',
    write: false,
    logLevel: 'error',
    external: ['react', 'react-dom', 'react-dom/server'],
  });
  // react must resolve from web/node_modules: run the bundle from a file next to it.
  const tmp = join(root, 'web', `.brand-icons-${process.pid}.cjs`);
  writeFileSync(tmp, built.outputFiles[0].text);
  try {
    const { execFileSync } = await import('node:child_process');
    const json = execFileSync(process.execPath, [tmp], { cwd: join(root, 'web'), encoding: 'utf8', maxBuffer: 1 << 28 });
    return JSON.parse(json);
  } finally {
    rmSync(tmp, { force: true });
  }
}

function attrs(s) {
  const o = {};
  for (const m of s.matchAll(/([\w:-]+)="([^"]*)"/g)) o[m[1]] = m[2];
  return o;
}

const NAMED = { gold: '#ffd700', white: '#ffffff', black: '#000000', red: '#ff0000', blue: '#0000ff', green: '#008000' };

/** CSS color -> Avalonia #AARRGGBB (Avalonia reads 4/8 digit hex as ARGB, CSS as RGBA). */
function color(c, opacity = 1) {
  let h = c.trim().toLowerCase();
  h = NAMED[h] ?? h;
  if (!h.startsWith('#')) throw new Error(`unsupported color ${c}`);
  h = h.slice(1);
  if (h.length === 3 || h.length === 4) h = [...h].map((x) => x + x).join('');
  const r = h.slice(0, 6);
  let a = h.length === 8 ? parseInt(h.slice(6, 8), 16) / 255 : 1;
  a = Math.round(a * opacity * 255);
  return `#${a.toString(16).padStart(2, '0')}${r}`.toUpperCase();
}

const num = (v) => (Math.round(v * 1000) / 1000).toString();

function ellipsePath(cx, cy, rx, ry) {
  return `M${num(cx - rx)},${num(cy)}A${num(rx)},${num(ry)} 0 1 0 ${num(cx + rx)},${num(cy)}A${num(rx)},${num(ry)} 0 1 0 ${num(cx - rx)},${num(cy)}Z`;
}

function parseSvg(svg) {
  const open = attrs(svg.match(/<svg\b([^>]*)>/)[1]);
  const rootFill = open.fill ?? 'currentColor';
  const rootRule = open['fill-rule'] ?? 'nonzero';
  const gradients = [];
  // gradients live in <defs> after the shapes that use them: number them first
  const ids = new Map();
  for (const m of svg.matchAll(/<linearGradient\b([^>]*)>/g)) ids.set(attrs(m[1]).id, ids.size);
  const layers = [];
  let current = null;
  for (const m of svg.matchAll(/<(path|circle|ellipse|linearGradient|stop|radialGradient|rect|g|polygon)\b([^>]*)>/g)) {
    const [, tag, rest] = m;
    const a = attrs(rest);
    if (tag === 'linearGradient') {
      current = {
        bbox: (a.gradientUnits ?? 'objectBoundingBox') !== 'userSpaceOnUse',
        x1: parseFloat(a.x1 ?? '0'),
        y1: parseFloat(a.y1 ?? '0'),
        x2: parseFloat(a.x2 ?? '1'),
        y2: parseFloat(a.y2 ?? '0'),
        stops: [],
      };
      gradients.push(current);
    } else if (tag === 'stop') {
      current.stops.push({ offset: parseFloat(a.offset ?? '0'), color: color(a['stop-color'] ?? '#000', parseFloat(a['stop-opacity'] ?? '1')) });
    } else if (tag === 'path' || tag === 'circle' || tag === 'ellipse') {
      const fill = a.fill ?? rootFill;
      if (fill === 'none') continue;
      let d;
      if (tag === 'path') d = a.d;
      else if (tag === 'circle') d = ellipsePath(+a.cx, +a.cy, +a.r, +a.r);
      else d = ellipsePath(+a.cx, +a.cy, +a.rx, +a.ry);
      const layer = {
        d,
        fill: null,
        gradient: -1,
        evenOdd: (a['fill-rule'] ?? rootRule) === 'evenodd',
        opacity: parseFloat(a.opacity ?? '1') * parseFloat(a['fill-opacity'] ?? '1'),
      };
      if (fill.startsWith('url(#')) layer.gradient = ids.get(fill.slice(5, -1)) ?? -1;
      else if (fill !== 'currentColor') layer.fill = color(fill);
      layers.push(layer);
    } else {
      throw new Error(`unsupported svg element <${tag}>`);
    }
  }
  return { layers, gradients };
}

const cs = (s) => JSON.stringify(s);

/** `linear-gradient(135deg, #a, #b 40%)` -> a bounding-box gradient literal (square tile), or null for a plain color. */
function cssGradient(bg) {
  const m = /^linear-gradient\((.*)\)$/s.exec(bg.trim());
  if (!m) return null;
  const parts = m[1].split(/,(?![^()]*\))/).map((x) => x.trim());
  let deg = 180;
  if (/^(to |-?\d)/.test(parts[0])) {
    const first = parts.shift();
    const dirs = { 'to top': 0, 'to right': 90, 'to bottom': 180, 'to left': 270 };
    deg = first in dirs ? dirs[first] : parseFloat(first);
  }
  const t = (deg * Math.PI) / 180;
  const dx = Math.sin(t);
  const dy = -Math.cos(t);
  const half = (Math.abs(dx) + Math.abs(dy)) / 2;
  const stops = parts.map((p, i) => {
    const sm = /^(#[0-9a-fA-F]+)\s*(\d+(?:\.\d+)?)?%?$/.exec(p);
    return { color: color(sm[1]), offset: sm[2] !== undefined ? parseFloat(sm[2]) / 100 : i / (parts.length - 1) };
  });
  return `new(true, ${num(0.5 - dx * half)}, ${num(0.5 - dy * half)}, ${num(0.5 + dx * half)}, ${num(0.5 + dy * half)}, [${stops.map((x) => `new(${num(x.offset)}, ${cs(x.color)})`).join(', ')}])`;
}

function emitMarks(out) {
  const lines = [];
  lines.push('// <auto-generated>');
  lines.push('// Generated by scripts/gen-desktop-brand-icons.mjs from @lobehub/icons (the marks web/src/components/icons.tsx draws).');
  lines.push('// Do not edit; rerun the script (`--check` verifies it). plan avalonia-client §21 WP0b.');
  lines.push('// </auto-generated>');
  lines.push('');
  lines.push('#nullable enable');
  lines.push('');
  lines.push('namespace Astra.Desktop.Components.Kit;');
  lines.push('');
  lines.push('internal static class BrandMarkData');
  lines.push('{');
  lines.push('    /// <summary>The flattened mark for a key like "OpenAI.Mono" / "Claude.Color" (24x24 view box), or null.</summary>');
  lines.push('    public static BrandMark? Create(string key) => key switch');
  lines.push('    {');
  const avatar = [];
  for (const n of names) {
    for (const v of ['Mono', 'Color']) {
      const svg = out[`${n}.${v}`];
      if (!svg) continue;
      const p = parseSvg(svg);
      const L = p.layers
        .map((l) => `new(${cs(l.d)}, ${l.fill === null ? 'null' : cs(l.fill)}, ${l.gradient}, ${l.evenOdd}, ${num(l.opacity)})`)
        .join(', ');
      const G = p.gradients
        .map(
          (g) =>
            `new(${g.bbox}, ${num(g.x1)}, ${num(g.y1)}, ${num(g.x2)}, ${num(g.y2)}, [${g.stops.map((s) => `new(${num(s.offset)}, ${cs(s.color)})`).join(', ')}])`,
        )
        .join(', ');
      lines.push(`        ${cs(`${n}.${v}`)} => new BrandMark([${L}], [${G}]),`);
    }
    const st = out[`${n}.style`];
    const avatarSrc = readFileSync(`${lobe}/${n}/components/Avatar.js`, 'utf8');
    const variant = /Icon: (Mono|Color)/.exec(avatarSrc)?.[1] ?? 'Mono';
    const bg = st.AVATAR_BACKGROUND ?? st.COLOR_PRIMARY;
    const grad = cssGradient(bg);
    avatar.push(
      `        ${cs(n)} => new(${cs(grad ? color(st.COLOR_PRIMARY) : color(bg))}, ${cs(color(st.AVATAR_COLOR ?? '#fff'))}, ${num(st.AVATAR_ICON_MULTIPLE ?? 0.75)}, ${cs(variant)}, ${grad ?? 'null'}),`,
    );
  }
  lines.push('        _ => null,');
  lines.push('    };');
  lines.push('');
  lines.push('    /// <summary>The avatar tile (background, foreground, icon scale, icon variant) lobehub draws for a brand, or null.</summary>');
  lines.push('    public static BrandAvatarSpec? Avatar(string brand) => brand switch');
  lines.push('    {');
  lines.push(...avatar);
  lines.push('        _ => null,');
  lines.push('    };');
  lines.push('}');
  lines.push('');
  return lines.join('\n');
}

// ---------- logos ----------

async function emitLogos() {
  const sharp = findPackage('sharp', 'sharp@');
  const lines = [];
  lines.push('// <auto-generated>');
  lines.push('// Generated by scripts/gen-desktop-brand-icons.mjs from the vendor logos in web/src/assets (128 px PNG, base64).');
  lines.push('// Do not edit; rerun the script (`--check` verifies it). plan avalonia-client §21 WP0b.');
  lines.push('// </auto-generated>');
  lines.push('');
  lines.push('#nullable enable');
  lines.push('');
  lines.push('namespace Astra.Desktop.Components.Kit;');
  lines.push('');
  lines.push('internal static class BrandLogoData');
  lines.push('{');
  lines.push('    /// <summary>The PNG bytes of a bundled vendor logo (keys: crush, droid, zed, ...), or null.</summary>');
  lines.push('    public static byte[]? Png(string key) => key switch');
  lines.push('    {');
  for (const [key, rel] of Object.entries(LOGOS)) {
    const file = join(root, rel);
    const input = rel.endsWith('.svg') ? sharp(readFileSync(file), { density: 288 }) : sharp(file);
    const buf = await input
      .resize(128, 128, { fit: 'contain', background: { r: 0, g: 0, b: 0, alpha: 0 } })
      .png({ compressionLevel: 9, palette: true, quality: 90 })
      .toBuffer();
    lines.push(`        ${cs(key)} => Convert.FromBase64String(${cs(buf.toString('base64'))}),`);
  }
  lines.push('        _ => null,');
  lines.push('    };');
  lines.push('}');
  lines.push('');
  return lines.join('\n');
}

const files = {
  'BrandMarkData.g.cs': emitMarks(await renderSvgs()),
  'BrandLogoData.g.cs': await emitLogos(),
};

mkdirSync(outDir, { recursive: true });
let stale = false;
for (const [name, text] of Object.entries(files)) {
  const target = join(outDir, name);
  if (check) {
    if (!existsSync(target) || readFileSync(target, 'utf8') !== text) {
      console.error(`${name} is out of date; run node scripts/gen-desktop-brand-icons.mjs`);
      stale = true;
    }
  } else {
    writeFileSync(target, text);
    console.log(`wrote ${target} (${(text.length / 1024).toFixed(0)} KB)`);
  }
}
if (stale) process.exit(1);
void pathToFileURL;
