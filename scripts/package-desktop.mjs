#!/usr/bin/env node
/**
 * Package the Avalonia desktop app (a Native AOT `dotnet publish` of src/Astra.Desktop) into the
 * installers the Electron app used to ship, under exactly the same names (plan avalonia-client §5, §7):
 *
 *   macOS    Astra-<ver>-<arch>.dmg, Astra-<ver>-<arch>.zip (Squirrel.Mac's update payload), latest-mac.yml
 *   Windows  Astra-<ver>-<arch>-setup.exe (NSIS, src/Astra.Desktop/Packaging/installer.nsi)
 *   Linux    Astra-<ver>-<x86_64|arm64>.AppImage
 *
 * plus <out>/unpacked/: the app laid out as installed (Astra.app on macOS, else the program directory with
 * resources/server), which scripts/pack-desktop-avalonia.mjs turns into the npm desktop package.
 *
 * Usage:
 *   node scripts/package-desktop.mjs --rid <rid> --publish-dir <dir> --server-dir <dir> --version <ver> [--out out/desktop]
 *
 *   --publish-dir  the `dotnet publish src/Astra.Desktop -p:PublishAot=true` output
 *   --server-dir   the server as packed for npm (npm/server-<platform>/bin: binary, companions, wwwroot/),
 *                  shipped inside the app as its bundled server (ServiceHost.FindBundledServer)
 *
 * The identity matches the Electron app so installed clients take the new one as an update and keep their
 * shortcuts: bundle id io.astra.desktop / Astra.app / executable Astra (macOS), Astra.exe and electron-builder's
 * uninstall key and install directory (Windows), astra (Linux).
 *
 * macOS signing: with CSC_LINK (a base64 .p12 or a path) + CSC_KEY_PASSWORD every Mach-O is signed with the
 * Developer ID identity and the hardened runtime, and with APPLE_ID + APPLE_APP_SPECIFIC_PASSWORD + APPLE_TEAM_ID
 * the app is notarized and stapled. Without them the bundle is signed ad hoc (a local test build; Gatekeeper and
 * Squirrel.Mac reject it) — pass --require-signing (release.yml does) to fail instead.
 * Linux: downloads the pinned appimagetool + runtime (sha256-checked) unless APPIMAGETOOL / APPIMAGE_RUNTIME point at them.
 * Windows: needs makensis on PATH (preinstalled on GitHub's Windows images) or MAKENSIS.
 */
import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const packaging = path.join(root, 'src', 'Astra.Desktop', 'Packaging');
const iconPng = path.join(root, 'desktop', 'assets', 'icon.png');

const APPIMAGE_TOOL = {
  version: '1.9.1',
  sha256: {
    x86_64: 'ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0',
    aarch64: 'f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158',
  },
};
const APPIMAGE_RUNTIME = {
  version: '20251108',
  sha256: {
    x86_64: '2fca8b443c92510f1483a883f60061ad09b46b978b2631c807cd873a47ec260d',
    aarch64: '00cbdfcf917cc6c0ff6d3347d59e0ca1f7f45a6df1a428a0d6d8a78664d87444',
  },
};

const arg = (name) => {
  const i = process.argv.indexOf(`--${name}`);
  return i === -1 ? undefined : process.argv[i + 1];
};
const flag = (name) => process.argv.includes(`--${name}`);
const fail = (msg) => {
  console.error(`package-desktop: ${msg}`);
  process.exit(1);
};
const run = (cmd, args, opts = {}) => {
  console.log(`$ ${cmd} ${args.map((a) => (/\s/.test(a) ? JSON.stringify(a) : a)).join(' ')}`);
  return execFileSync(cmd, args, { stdio: 'inherit', ...opts });
};
const retry = (attempts, what, fn) => {
  for (let i = 1; ; i++) {
    try {
      return fn();
    } catch (err) {
      if (i >= attempts) throw err;
      console.warn(`::warning::${what} failed (attempt ${i} of ${attempts}) — retrying in 30 s.`);
      execFileSync('sleep', ['30']);
    }
  }
};

const rid = arg('rid');
const publishDir = arg('publish-dir') && path.resolve(arg('publish-dir'));
const serverDir = arg('server-dir') && path.resolve(arg('server-dir'));
const version = arg('version');
const outDir = path.resolve(arg('out') ?? path.join(root, 'out', 'desktop'));
if (!rid || !publishDir || !serverDir || !version) {
  fail('Usage: node scripts/package-desktop.mjs --rid <rid> --publish-dir <dir> --server-dir <dir> --version <ver> [--out <dir>]');
}
for (const [what, dir] of [['publish dir', publishDir], ['server dir', serverDir]]) {
  if (!fs.existsSync(dir)) fail(`${what} not found: ${dir}`);
}
const isMac = rid.startsWith('osx');
const isWin = rid.startsWith('win');
const arm = rid.endsWith('-arm64');
const builtExe = isWin ? 'astra-desktop.exe' : 'astra-desktop';
if (!fs.existsSync(path.join(publishDir, builtExe))) fail(`${builtExe} missing from ${publishDir}`);

fs.rmSync(outDir, { recursive: true, force: true });
fs.mkdirSync(outDir, { recursive: true });
const unpacked = path.join(outDir, 'unpacked');

/** The publish output without debug symbols, the executable renamed to `exeName`. */
function copyProgram(dest, exeName) {
  fs.mkdirSync(dest, { recursive: true });
  for (const e of fs.readdirSync(publishDir, { withFileTypes: true })) {
    if (/\.(pdb|dbg|dSYM)$/i.test(e.name)) continue;
    const target = e.name === builtExe ? exeName : e.name;
    fs.cpSync(path.join(publishDir, e.name), path.join(dest, target), { recursive: true, verbatimSymlinks: true });
  }
  fs.chmodSync(path.join(dest, exeName), 0o755);
}

function copyServer(resourcesDir) {
  const dest = path.join(resourcesDir, 'server');
  fs.cpSync(serverDir, dest, { recursive: true, verbatimSymlinks: true });
  const exe = path.join(dest, isWin ? 'astra-server.exe' : 'astra-server');
  if (!fs.existsSync(exe)) fail(`server binary missing from ${serverDir}`);
  if (!isWin) fs.chmodSync(exe, 0o755);
}

function sha512b64(file) {
  return crypto.createHash('sha512').update(fs.readFileSync(file)).digest('base64');
}

function download(url, dest, sha256) {
  run('curl', ['-fsSL', '--retry', '3', '-o', dest, url]);
  const got = crypto.createHash('sha256').update(fs.readFileSync(dest)).digest('hex');
  if (got !== sha256) fail(`sha256 mismatch for ${url}: ${got}`);
  fs.chmodSync(dest, 0o755);
}

// ---------------------------------------------------------------- macOS
function macSigning() {
  const { CSC_LINK, CSC_KEY_PASSWORD } = process.env;
  if (!CSC_LINK || !CSC_KEY_PASSWORD) {
    if (flag('require-signing')) fail('CSC_LINK / CSC_KEY_PASSWORD are not set');
    console.warn('::warning::No signing certificate: the app is signed ad hoc (local test build only).');
    return { identity: '-', cleanup: () => {} };
  }
  const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'astra-sign-'));
  const p12 = fs.existsSync(CSC_LINK) ? CSC_LINK : path.join(tmp, 'cert.p12');
  if (p12 !== CSC_LINK) fs.writeFileSync(p12, Buffer.from(CSC_LINK, 'base64'));
  const keychain = path.join(tmp, 'astra-build.keychain-db');
  const pass = crypto.randomBytes(16).toString('hex');
  const previous = execFileSync('security', ['list-keychains', '-d', 'user'], { encoding: 'utf8' })
    .split('\n').map((l) => l.trim().replace(/^"|"$/g, '')).filter(Boolean);
  run('security', ['create-keychain', '-p', pass, keychain]);
  run('security', ['set-keychain-settings', '-lut', '21600', keychain]);
  run('security', ['unlock-keychain', '-p', pass, keychain]);
  execFileSync('security', ['import', p12, '-k', keychain, '-P', CSC_KEY_PASSWORD, '-T', '/usr/bin/codesign'], { stdio: 'inherit' });
  execFileSync('security', ['set-key-partition-list', '-S', 'apple-tool:,apple:,codesign:', '-s', '-k', pass, keychain], { stdio: 'ignore' });
  run('security', ['list-keychains', '-d', 'user', '-s', keychain, ...previous]);
  const ids = execFileSync('security', ['find-identity', '-v', '-p', 'codesigning', keychain], { encoding: 'utf8' });
  const m = ids.match(/"(Developer ID Application:[^"]+)"/);
  if (!m) fail(`no "Developer ID Application" identity in the certificate:\n${ids}`);
  return {
    identity: m[1],
    keychain,
    cleanup: () => {
      try {
        execFileSync('security', ['list-keychains', '-d', 'user', '-s', ...previous]);
        execFileSync('security', ['delete-keychain', keychain]);
      } catch {}
      fs.rmSync(tmp, { recursive: true, force: true });
    },
  };
}

function isMachO(file) {
  const fd = fs.openSync(file, 'r');
  const b = Buffer.alloc(4);
  fs.readSync(fd, b, 0, 4, 0);
  fs.closeSync(fd);
  const magic = b.readUInt32BE(0);
  return [0xfeedface, 0xfeedfacf, 0xcefaedfe, 0xcffaedfe, 0xcafebabe].includes(magic);
}

function walkFiles(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walkFiles(p, out);
    else if (e.isFile()) out.push(p);
  }
  return out;
}

function packageMac() {
  const arch = arm ? 'arm64' : 'x64';
  const app = path.join(unpacked, 'Astra.app');
  const contents = path.join(app, 'Contents');
  copyProgram(path.join(contents, 'MacOS'), 'Astra');
  fs.mkdirSync(path.join(contents, 'Resources'), { recursive: true });
  fs.copyFileSync(path.join(packaging, 'Astra.icns'), path.join(contents, 'Resources', 'Astra.icns'));
  copyServer(path.join(contents, 'Resources'));
  // Bundle versions are digits and dots only: a preview carries its stable's x.y.z.
  const plist = fs.readFileSync(path.join(packaging, 'Info.plist'), 'utf8').replaceAll('@VERSION@', version.split('-')[0]);
  fs.writeFileSync(path.join(contents, 'Info.plist'), plist);
  fs.writeFileSync(path.join(contents, 'PkgInfo'), 'APPL????');

  const signing = macSigning();
  try {
    const entitlements = path.join(packaging, 'entitlements.mac.plist');
    const keychainArgs = signing.keychain ? ['--keychain', signing.keychain] : [];
    const realSign = signing.identity !== '-';
    const sign = (target, withEntitlements) => run('codesign', [
      '--force', '--sign', signing.identity, ...keychainArgs,
      ...(realSign ? ['--timestamp', '--options', 'runtime'] : []),
      ...(withEntitlements ? ['--entitlements', entitlements] : []),
      target,
    ]);
    // Inside out: every nested Mach-O (the native libraries, the bundled server and its companions), then the app.
    const main = path.join(contents, 'MacOS', 'Astra');
    for (const f of walkFiles(contents).filter((f) => f !== main && isMachO(f))) {
      sign(f, fs.statSync(f).mode & 0o111 && !f.endsWith('.dylib'));
    }
    sign(app, true);
    run('codesign', ['--verify', '--deep', '--strict', '--verbose=2', app]);

    const { APPLE_ID, APPLE_APP_SPECIFIC_PASSWORD, APPLE_TEAM_ID } = process.env;
    if (realSign && APPLE_ID && APPLE_APP_SPECIFIC_PASSWORD && APPLE_TEAM_ID) {
      const submission = path.join(outDir, 'notarize.zip');
      run('ditto', ['-c', '-k', '--sequesterRsrc', '--keepParent', app, submission]);
      retry(3, 'notarytool', () => run('xcrun', ['notarytool', 'submit', submission,
        '--apple-id', APPLE_ID, '--password', APPLE_APP_SPECIFIC_PASSWORD, '--team-id', APPLE_TEAM_ID, '--wait']));
      fs.rmSync(submission);
      // stapler looks the ticket up on CloudKit, which drops connections now and then (0.9.5's stapler error 68).
      retry(5, 'stapler', () => run('xcrun', ['stapler', 'staple', app]));
      run('spctl', ['--assess', '--type', 'execute', '--verbose', app]);
    } else if (realSign) {
      if (flag('require-signing')) fail('APPLE_ID / APPLE_APP_SPECIFIC_PASSWORD / APPLE_TEAM_ID are not set');
      console.warn('::warning::Signed but not notarized: Gatekeeper will refuse it on other machines.');
    }

    const zip = path.join(outDir, `Astra-${version}-${arch}.zip`);
    run('ditto', ['-c', '-k', '--sequesterRsrc', '--keepParent', app, zip]);

    const dmg = path.join(outDir, `Astra-${version}-${arch}.dmg`);
    const dmgRoot = path.join(outDir, 'dmg-root');
    fs.mkdirSync(dmgRoot);
    run('ditto', [app, path.join(dmgRoot, 'Astra.app')]);
    fs.symlinkSync('/Applications', path.join(dmgRoot, 'Applications'));
    retry(3, 'hdiutil', () => run('hdiutil', ['create', '-volname', `Astra ${version}`, '-srcfolder', dmgRoot,
      '-ov', '-format', 'UDZO', '-fs', 'HFS+', dmg]));
    fs.rmSync(dmgRoot, { recursive: true, force: true });
    if (realSign) sign(dmg, false);

    // The same shape electron-builder wrote; the docs site renders its own copy from the uploaded zips.
    const date = new Date().toISOString();
    const entry = (f) => `  - url: ${path.basename(f)}\n    sha512: ${sha512b64(f)}\n    size: ${fs.statSync(f).size}`;
    fs.writeFileSync(path.join(outDir, 'latest-mac.yml'), [
      `version: ${version}`, 'files:', entry(zip), entry(dmg),
      `path: ${path.basename(zip)}`, `sha512: ${sha512b64(zip)}`, `releaseDate: '${date}'`, '',
    ].join('\n'));
  } finally {
    signing.cleanup();
  }
}

// ---------------------------------------------------------------- Windows
function packageWin() {
  const arch = arm ? 'arm64' : 'x64';
  copyProgram(unpacked, 'Astra.exe');
  copyServer(path.join(unpacked, 'resources'));
  const makensis = process.env.MAKENSIS
    ?? ['C:\\Program Files (x86)\\NSIS\\makensis.exe', 'C:\\Program Files\\NSIS\\makensis.exe'].find((p) => fs.existsSync(p))
    ?? 'makensis';
  const [a, b, c] = version.split('-')[0].split('.');
  const n = /-preview\.(\d+)$/.exec(version)?.[1] ?? '0';
  run(makensis, [
    '-V3', '-INPUTCHARSET', 'UTF8',
    `-DSOURCE_DIR=${unpacked}`,
    `-DVERSION=${version}`,
    `-DFILE_VERSION=${a}.${b}.${c}.${n}`,
    `-DOUTFILE=${path.join(outDir, `Astra-${version}-${arch}-setup.exe`)}`,
    `-DICON=${path.join(packaging, 'Astra.ico')}`,
    path.join(packaging, 'installer.nsi'),
  ]);
}

// ---------------------------------------------------------------- Linux
function packageLinux() {
  const arch = arm ? 'aarch64' : 'x86_64';
  copyProgram(unpacked, 'astra');
  copyServer(path.join(unpacked, 'resources'));

  const appDir = path.join(outDir, 'Astra.AppDir');
  fs.cpSync(unpacked, appDir, { recursive: true, verbatimSymlinks: true });
  fs.copyFileSync(path.join(packaging, 'astra.desktop'), path.join(appDir, 'astra.desktop'));
  fs.copyFileSync(iconPng, path.join(appDir, 'astra.png'));
  fs.copyFileSync(iconPng, path.join(appDir, '.DirIcon'));
  fs.writeFileSync(path.join(appDir, 'AppRun'), '#!/bin/sh\nHERE="$(dirname "$(readlink -f "$0")")"\nexec "$HERE/astra" "$@"\n');
  fs.chmodSync(path.join(appDir, 'AppRun'), 0o755);

  const tools = path.join(outDir, 'tools');
  fs.mkdirSync(tools);
  let tool = process.env.APPIMAGETOOL;
  if (!tool) {
    tool = path.join(tools, 'appimagetool');
    download(`https://github.com/AppImage/appimagetool/releases/download/${APPIMAGE_TOOL.version}/appimagetool-${arch}.AppImage`,
      tool, APPIMAGE_TOOL.sha256[arch]);
  }
  let runtime = process.env.APPIMAGE_RUNTIME;
  if (!runtime) {
    runtime = path.join(tools, 'runtime');
    download(`https://github.com/AppImage/type2-runtime/releases/download/${APPIMAGE_RUNTIME.version}/runtime-${arch}`,
      runtime, APPIMAGE_RUNTIME.sha256[arch]);
  }
  const fileArch = arm ? 'arm64' : 'x86_64';
  run(tool, ['--no-appstream', '--runtime-file', runtime, appDir, path.join(outDir, `Astra-${version}-${fileArch}.AppImage`)], {
    // No FUSE on CI runners: the tool (itself an AppImage) unpacks itself instead of mounting.
    env: { ...process.env, ARCH: arch, APPIMAGE_EXTRACT_AND_RUN: '1' },
  });
  fs.rmSync(appDir, { recursive: true, force: true });
  fs.rmSync(tools, { recursive: true, force: true });
}

if (isMac) packageMac();
else if (isWin) packageWin();
else packageLinux();

console.log(`\nPackaged ${rid} ${version}:`);
for (const f of fs.readdirSync(outDir)) console.log(`  ${f}`);
