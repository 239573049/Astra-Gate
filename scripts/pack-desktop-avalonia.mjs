#!/usr/bin/env node
/**
 * Lay an Avalonia (Native AOT) desktop publish out as npm/desktop-<platform>-<arch>/app/.
 *
 * Usage:
 *   node scripts/pack-desktop-avalonia.mjs --rid <rid> --publish-dir <dir> [--app <Astra.app>] [--out <appDir>]
 *
 *   --publish-dir  the app as installed: scripts/package-desktop.mjs's out/desktop/unpacked (win/linux:
 *                  Astra.exe / astra + resources/server), or for macOS a dir that contains the assembled,
 *                  signed Astra.app (or pass --app directly)
 *   --out          override the destination app/ dir (dry runs; default npm/<pkg>/app)
 *
 * Why the layout is constrained (.plan/avalonia-client.md §5.2): already-installed
 * clients (old CLI, the Electron app's bundled packages/update-core) run the OLD
 * findDesktopEntry heuristic on this NEW package. That heuristic picks:
 *   - macOS:   the shallowest *.app under app/ (depth <= 4) -> exactly one, no nested .app
 *   - Windows: the shallowest *.exe (depth <= 3) not matching crashpad|installer|unins|squirrel
 *              -> exactly one .exe, at the root
 *   - Linux:   the shallowest +x file (depth <= 3) not ending .so/.png/.json/.sh/.txt/.desktop
 *              -> the main binary is the only executable file; companions lose +x
 * This script enforces those invariants (fails loudly) and writes app/astra-desktop.json,
 * which new update-core reads first. As with the Electron packages, the server
 * (resources/server) is dropped on win/linux (npm size limit, CLI-managed server);
 * macOS keeps whatever is inside the .app (sealed by the code signature).
 */
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ridToDesktopDir } from './rid-map.mjs';

const arg = (name) => {
  const i = process.argv.indexOf(`--${name}`);
  return i === -1 ? undefined : process.argv[i + 1];
};
const fail = (msg) => {
  console.error(`pack-desktop-avalonia: ${msg}`);
  process.exit(1);
};

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const rid = arg('rid');
const publishArg = arg('publish-dir');
if (!rid || !publishArg) {
  fail('Usage: node scripts/pack-desktop-avalonia.mjs --rid <rid> --publish-dir <dir> [--app <Astra.app>] [--out <appDir>]');
}
const desktopDir = ridToDesktopDir(rid);
const isMac = rid.startsWith('osx');
const isWin = rid.startsWith('win');
const publishDir = path.resolve(publishArg);
if (!fs.existsSync(publishDir)) fail(`publish dir not found: ${publishDir}`);
const appDir = arg('out') ? path.resolve(arg('out')) : path.join(root, 'npm', desktopDir, 'app');

function walk(dir, depth, out = []) {
  if (depth < 0) return out;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) {
      out.push({ p, dir: true });
      walk(p, depth - 1, out);
    } else if (e.isFile()) out.push({ p, dir: false });
  }
  return out;
}

fs.rmSync(appDir, { recursive: true, force: true });
fs.mkdirSync(appDir, { recursive: true });

let entry;
let kind;
if (isMac) {
  let bundle = arg('app') && path.resolve(arg('app'));
  if (!bundle) {
    const found = fs.readdirSync(publishDir, { withFileTypes: true }).filter((e) => e.isDirectory() && e.name.endsWith('.app'));
    if (found.length !== 1) fail(`expected exactly one .app in ${publishDir}, found ${found.length}; pass --app`);
    bundle = path.join(publishDir, found[0].name);
  }
  if (!fs.existsSync(bundle)) fail(`app bundle not found: ${bundle}`);
  entry = path.basename(bundle);
  kind = 'app-bundle';
  fs.cpSync(bundle, path.join(appDir, entry), { recursive: true, verbatimSymlinks: true });
} else {
  const server = path.join(publishDir, 'resources', 'server');
  const keep = (src) => src !== server && !src.startsWith(server + path.sep);
  for (const e of fs.readdirSync(publishDir, { withFileTypes: true })) {
    fs.cpSync(path.join(publishDir, e.name), path.join(appDir, e.name), {
      recursive: true,
      verbatimSymlinks: true,
      filter: keep,
    });
  }
  // The Electron app's names, so launchers and shortcuts made for it keep working.
  entry = isWin ? 'Astra.exe' : 'astra';
  kind = 'exe';
  if (!fs.existsSync(path.join(appDir, entry))) fail(`${entry} missing from ${publishDir}`);
  if (!isWin) {
    // Only the main binary may be executable (old heuristic picks the first +x file).
    for (const f of walk(appDir, 64).filter((x) => !x.dir)) {
      fs.chmodSync(f.p, path.basename(f.p) === entry && path.dirname(f.p) === appDir ? 0o755 : 0o644);
    }
  }
}
fs.writeFileSync(path.join(appDir, 'astra-desktop.json'), JSON.stringify({ entry, kind }, null, 2) + '\n');

// ---- assertions: the old findDesktopEntry must still pick `entry` ----
const problems = [];
if (isMac) {
  const apps = walk(appDir, 64).filter((x) => x.dir && x.p.endsWith('.app'));
  if (apps.length !== 1) problems.push(`expected exactly one .app (no nested), found ${apps.length}: ${apps.map((a) => path.relative(appDir, a.p)).join(', ')}`);
  else if (path.relative(appDir, apps[0].p) !== entry) problems.push(`.app is not at the root of app/: ${path.relative(appDir, apps[0].p)}`);
} else if (isWin) {
  const exes = walk(appDir, 64).filter((x) => !x.dir && /\.exe$/i.test(x.p));
  if (exes.length !== 1 || path.relative(appDir, exes[0].p) !== entry) {
    problems.push(`expected only ${entry} as .exe, found: ${exes.map((a) => path.relative(appDir, a.p)).join(', ') || 'none'}`);
  }
} else {
  const xs = walk(appDir, 64).filter((x) => !x.dir && (fs.statSync(x.p).mode & 0o111) !== 0);
  if (xs.length !== 1 || path.relative(appDir, xs[0].p) !== entry) {
    problems.push(`expected only ${entry} to be executable, found: ${xs.map((a) => path.relative(appDir, a.p)).join(', ') || 'none'}`);
  }
}
if (problems.length) fail(`old findDesktopEntry invariants violated (plan §5.2):\n  - ${problems.join('\n  - ')}`);
console.log(`Packed ${desktopDir}/app (${kind}: ${entry}) -> ${appDir}`);
