/**
 * Best-effort "is this pid alive" check that works on macOS, Linux and
 * Windows. signal 0 does not actually signal; it only probes existence.
 */
export function isPidAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0) return false;
  try {
    process.kill(pid, 0);
    return true;
  } catch (err) {
    // EPERM means the process exists but belongs to another user.
    if (err instanceof Error && 'code' in err && (err as NodeJS.ErrnoException).code === 'EPERM') {
      return true;
    }
    return false;
  }
}
