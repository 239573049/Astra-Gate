/**
 * The desktop app expects the server's `apiVersion` to be in the "1.x" family.
 * A different MAJOR version means the server and desktop build are out of
 * sync and the user should run `astra update`.
 */
export const EXPECTED_API_MAJOR = 1;

/** Returns the MAJOR part of an apiVersion string like "1.4" or "2", or null. */
export function majorOf(apiVersion: string): number | null {
  const m = /^\s*(\d+)(?:\.|$)/.exec(apiVersion);
  const group = m?.[1];
  return group ? Number.parseInt(group, 10) : null;
}

export function isApiVersionCompatible(
  serverApiVersion: string,
  expectedMajor: number = EXPECTED_API_MAJOR,
): boolean {
  const major = majorOf(serverApiVersion);
  return major !== null && major === expectedMajor;
}
