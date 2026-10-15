#!/usr/bin/env node
/**
 * The release channel rule, shared by scripts/sync-versions.mjs and release.yml.
 *
 * A release version is either
 *   x.y.z              stable  — npm dist-tag `latest`, feed channel `stable`
 *   x.y.z-preview.N    preview — npm dist-tag `preview`, feed channel `preview` (N >= 1)
 * and nothing else: the channel is derived from the version, never chosen separately.
 * A preview leads up to the stable x.y.z it names (0.10.0-preview.2 < 0.10.0).
 *
 *   node scripts/release-channel.mjs <version> [--preview-tag <version>]
 *
 * prints GITHUB_OUTPUT lines and exits 1 for a version outside the rule:
 *   channel=stable|preview
 *   npm_tag=latest|preview
 *   tag_preview=true|false   whether the `preview` npm dist-tag / `:preview` image move to this
 *                            version: always for a preview; for a stable only when it is newer
 *                            than --preview-tag (the current `preview` dist-tag), so preview users
 *                            reach the stable that follows without a later preview falling back.
 */
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const RELEASE_VERSION = /^(\d+)\.(\d+)\.(\d+)(?:-preview\.([1-9]\d*))?$/;

/** { core: [major, minor, patch], preview: N | null }, or null outside the rule. */
export function parseReleaseVersion(version) {
  const m = RELEASE_VERSION.exec(String(version ?? '').trim());
  if (!m) return null;
  return { core: [Number(m[1]), Number(m[2]), Number(m[3])], preview: m[4] ? Number(m[4]) : null };
}

export function isReleaseVersion(version) {
  return parseReleaseVersion(version) !== null;
}

/** 'stable' | 'preview'; throws for a version outside the rule. */
export function channelOf(version) {
  const v = parseReleaseVersion(version);
  if (!v) throw new Error(`"${version}" is not a release version (x.y.z or x.y.z-preview.N).`);
  return v.preview === null ? 'stable' : 'preview';
}

export function npmTagOf(channel) {
  return channel === 'preview' ? 'preview' : 'latest';
}

/** Semver precedence for release versions: >0 when a is newer. Unparsable versions sort lowest. */
export function compareReleaseVersions(a, b) {
  const pa = parseReleaseVersion(a);
  const pb = parseReleaseVersion(b);
  if (!pa || !pb) return pa ? 1 : pb ? -1 : 0;
  for (let i = 0; i < 3; i++) {
    if (pa.core[i] !== pb.core[i]) return pa.core[i] - pb.core[i];
  }
  if (pa.preview === pb.preview) return 0;
  if (pa.preview === null) return 1;
  if (pb.preview === null) return -1;
  return pa.preview - pb.preview;
}

function main() {
  const version = process.argv[2];
  const i = process.argv.indexOf('--preview-tag');
  const previewTag = i !== -1 ? (process.argv[i + 1] ?? '').trim() : '';
  if (!version || version.startsWith('--')) {
    console.error('Usage: release-channel.mjs <version> [--preview-tag <version>]');
    process.exit(1);
  }
  let channel;
  try {
    channel = channelOf(version);
  } catch (err) {
    console.error(`::error::${err.message}`);
    process.exit(1);
  }
  const tagPreview =
    channel === 'preview' || !isReleaseVersion(previewTag) || compareReleaseVersions(version, previewTag) > 0;
  process.stdout.write(`channel=${channel}\nnpm_tag=${npmTagOf(channel)}\ntag_preview=${tagPreview}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) main();
