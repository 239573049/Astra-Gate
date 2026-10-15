/**
 * Version comparison helpers shared by the CLI and the desktop app.
 * Semver-shaped (major.minor.patch) with semver precedence for -prerelease
 * suffixes (+build is ignored), mirroring the server's UpdateCheckService.IsNewer:
 * 0.10.0 > 0.10.0-preview.2 > 0.10.0-preview.1 > 0.9.5.
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

/** The dot-separated prerelease identifiers of a version ("0.10.0-preview.2" → ["preview", "2"]); [] for a release. */
function prereleaseOf(v: string): string[] {
  const text = v.trim().split('+', 1)[0]!;
  const dash = text.indexOf('-');
  return dash === -1 ? [] : text.slice(dash + 1).split('.');
}

/** Semver precedence: >0 when a is newer, <0 when older, 0 when equal; null when either is unparsable. */
export function compareVersions(a: string | null | undefined, b: string | null | undefined): number | null {
  const ca = parseVersion(a);
  const cb = parseVersion(b);
  if (!ca || !cb) return null;
  for (let i = 0; i < 3; i++) {
    if (ca[i] !== cb[i]) return ca[i]! > cb[i]! ? 1 : -1;
  }
  const pa = prereleaseOf(a!);
  const pb = prereleaseOf(b!);
  // A release ranks above any prerelease of the same core.
  if (pa.length === 0 || pb.length === 0) return pb.length - pa.length;
  for (let i = 0; i < Math.min(pa.length, pb.length); i++) {
    const x = pa[i]!;
    const y = pb[i]!;
    if (x === y) continue;
    const nx = /^\d+$/.test(x);
    const ny = /^\d+$/.test(y);
    if (nx && ny) return Number(x) > Number(y) ? 1 : -1;
    if (nx !== ny) return nx ? -1 : 1; // numeric identifiers sort below alphanumeric ones
    return x > y ? 1 : -1;
  }
  return pa.length - pb.length;
}

/** True when candidate is strictly newer than current; unparsable inputs never count as newer. */
export function isNewerVersion(candidate: string | null | undefined, current: string): boolean {
  return (compareVersions(candidate, current) ?? 0) > 0;
}

/** True for a preview release (x.y.z-preview.N). */
export function isPreviewVersion(v: string | null | undefined): boolean {
  return /^v?\d+\.\d+\.\d+-preview\.\d+$/.test((v ?? '').trim());
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
