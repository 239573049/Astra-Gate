#!/usr/bin/env node
/**
 * Fail a macOS Native AOT binary that would not start on macOS 13 (issue #27). No CI image runs macOS 13 any more and
 * dyld reports only the first missing symbol, so this checks what dyld would check: the deployment target, and every
 * strong import and dylib against the macOS 13.3 SDK's .tbd stubs. Nothing in the SDK archive is executed.
 *
 * A symbol this names is new in a runtime pack: forward it in src/MacOsFloor/swift-lookup-shim.c if an older entry
 * point really is equivalent, otherwise the floor has to move (Info.plist, MacOsFloor.targets, the docs).
 *
 *   node scripts/check-macos-floor.mjs <binary>...
 *   node scripts/check-macos-floor.mjs --sdk <MacOSX13.3.sdk> <binary>...
 */
import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const FLOOR = '13.0';
const SDK_URL = 'https://github.com/joseluisq/macosx-sdks/releases/download/13.3/MacOSX13.3.sdk.tar.xz';
const SDK_SHA256 = '518e35eae6039b3f64e8025f4525c1c43786cc5cf39459d609852faf091e34be';

const run = (cmd, ...args) => execFileSync(cmd, args, { encoding: 'utf8', maxBuffer: 1 << 28 });

function fetchSdk() {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'macos-floor-'));
  const archive = path.join(dir, 'sdk.tar.xz');
  // curl, not fetch: it has a deadline and retries, so a stalled connection cannot hang a release.
  run('curl', '-fsSL', '--retry', '3', '--connect-timeout', '20', '--max-time', '300', '-o', archive, SDK_URL);
  const hash = crypto.createHash('sha256').update(fs.readFileSync(archive)).digest('hex');
  if (hash !== SDK_SHA256) throw new Error(`${SDK_URL}: sha256 ${hash}, expected ${SDK_SHA256}`);
  run('tar', '-xJf', archive, '-C', dir, '--include=*.tbd');
  return path.join(dir, 'MacOSX13.3.sdk');
}

/** Everything the SDK's stubs export, and the install names of its dylibs. */
function readSdk(root) {
  const symbols = new Set();
  const dylibs = new Set();
  const walk = (dir) => {
    for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
      const p = path.join(dir, entry.name);
      if (entry.isDirectory()) walk(p);
      else if (entry.isFile() && entry.name.endsWith('.tbd')) {
        const text = fs.readFileSync(p, 'utf8');
        for (const m of text.matchAll(/install-name:\s*'?([^'\n]+?)'?\s*$/gm)) dylibs.add(m[1]);
        for (const token of text.split(/[\s,[\]'":]+/)) if (token) symbols.add(token);
      }
    }
  };
  walk(root);
  return { symbols, dylibs };
}

/** Objective-C classes are listed by bare name in a .tbd. */
function exported(symbols, name) {
  const objc = name.match(/^_OBJC_(?:CLASS|METACLASS|EHTYPE)_\$_(.+)$/);
  return symbols.has(name) || (objc !== null && symbols.has(objc[1]));
}

function check(binary, sdk) {
  const problems = [];
  const minos = run('vtool', '-show-build', binary).match(/minos (\S+)/)?.[1];
  if (minos !== FLOOR) problems.push(`deployment target is ${minos}, expected ${FLOOR}`);

  for (const command of run('otool', '-l', binary).split('Load command').slice(1)) {
    const m = command.match(/cmd (LC_LOAD_DYLIB|LC_REEXPORT_DYLIB)\b[\s\S]*?name (\S+)/);
    if (m && !sdk.dylibs.has(m[2])) problems.push(`needs a dylib macOS 13 lacks: ${m[2]}`);
  }

  // A weak import may be absent at run time, and __swift_FORCE_LOAD_* are only ever weak autolink markers.
  const imports = run('nm', '-m', '-u', binary).split('\n').map((line) => line.trim()).filter(Boolean);
  for (const line of imports) {
    const m = line.match(/^\(undefined\) (weak )?external (?:\[[^\]]*\] )?(\S+)/);
    if (!m) problems.push(`unreadable nm line: ${line}`);
    else if (!m[1] && !exported(sdk.symbols, m[2])) problems.push(`imports a symbol macOS 13 lacks: ${line}`);
  }

  for (const problem of problems) console.error(`::error::${path.basename(binary)}: ${problem}`);
  if (problems.length === 0) console.log(`${binary}: ok on macOS ${FLOOR} (${imports.length} imports)`);
  return problems.length === 0;
}

const args = process.argv.slice(2);
const sdkFlag = args.indexOf('--sdk');
const sdkRoot = sdkFlag >= 0 ? args.splice(sdkFlag, 2)[1] : null;
if (args.length === 0) {
  console.error('Usage: check-macos-floor.mjs [--sdk <MacOSX13.3.sdk>] <binary>...');
  process.exit(1);
}
const sdk = readSdk(sdkRoot ?? fetchSdk());
const results = args.map((binary) => check(binary, sdk));
process.exit(results.every(Boolean) ? 0 : 1);
