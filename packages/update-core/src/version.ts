/**
 * Version comparison helpers shared by the CLI and the desktop app.
 * Semver-shaped (major.minor.patch), tolerant of -prerelease and +build suffixes
 * which are ignored for ordering, mirroring the server's UpdateCheckService.IsNewer.
 */
export function parseVersion(v: string | null | undefined): [number, number, number] | null {
  if (!v) return null;
  const core = v
    .trim()
    .split('-', 1)[0]!
    .split('+', 1)[0]!
    .replace(/^v/, '');
  const m = /^(\d+)(?:\.(\d+))?(?:\.(\d+))?$/.exec(core);
  if (!m) return null;
  return [Number(m[1]), Number(m[2] ?? 0), Number(m[3] ?? 0)];
}

/** True when candidate is strictly newer than current; unparsable inputs never count as newer. */
export function isNewerVersion(candidate: string | null | undefined, current: string): boolean {
  const c = parseVersion(candidate);
  const cur = parseVersion(current);
  if (!c || !cur) return false;
  for (let i = 0; i < 3; i++) {
    const a = c[i]!;
    const b = cur[i]!;
    if (a !== b) return a > b;
  }
  return false;
}

/** Returns the MAJOR part of a version-ish string like "1.4" or "2", or null. */
export function majorOf(v: string | null | undefined): number | null {
  const m = /^\s*(\d+)(?:\.|$)/.exec(v ?? '');
  const group = m?.[1];
  return group ? Number.parseInt(group, 10) : null;
}

/**
 * The desktop/CLI expect the server's apiVersion to stay within the same major.
 * A different major means the two builds are out of sync and only a manual,
 * confirmed update is allowed.
 */
export function isApiVersionCompatible(
  serverApiVersion: string | null | undefined,
  expectedMajor: number,
): boolean {
  const major = majorOf(serverApiVersion);
  return major !== null && major === expectedMajor;
}
