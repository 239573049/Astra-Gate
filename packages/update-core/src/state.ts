import fs from 'node:fs';
import path from 'node:path';

/** Executor phases; persisted verbatim so the CLI, desktop app and UI can render progress. */
export type UpdatePhase =
  | 'idle'
  | 'checking'
  | 'downloading'
  | 'swapping'
  | 'restarting'
  | 'rolling-back'
  | 'failed';

export interface UpdateState {
  phase: UpdatePhase;
  /** Version we are moving to (absent on idle). */
  targetVersion?: string;
  /** Version we started from. */
  previousVersion?: string;
  /** Human-readable detail for the UI ("verifying checksum…"). */
  step?: string;
  /** Last failure message; null/absent on success. */
  error?: string | null;
  /** Consecutive failed runs; reaching the cap locks out further automatic attempts. */
  failedCount: number;
  updatedAt: string;
}

/** After this many consecutive failed runs, `astra update` requires --retry to try again. */
export const MAX_CONSECUTIVE_FAILURES = 2;

const KNOWN_PHASES: readonly UpdatePhase[] = [
  'idle',
  'checking',
  'downloading',
  'swapping',
  'restarting',
  'rolling-back',
  'failed',
];

export function initialState(now: () => Date = () => new Date()): UpdateState {
  return { phase: 'idle', failedCount: 0, updatedAt: now().toISOString() };
}

/** Tolerant read: a missing, empty or corrupt state file is treated as "no state". */
export function readUpdateState(file: string): UpdateState | null {
  let text: string;
  try {
    text = fs.readFileSync(file, 'utf8');
  } catch {
    return null;
  }
  if (!text.trim()) return null;
  try {
    const raw = JSON.parse(text) as Partial<UpdateState> | null;
    if (!raw || typeof raw !== 'object' || !KNOWN_PHASES.includes(raw.phase as UpdatePhase)) return null;
    return {
      phase: raw.phase as UpdatePhase,
      ...(raw.targetVersion !== undefined ? { targetVersion: raw.targetVersion } : {}),
      ...(raw.previousVersion !== undefined ? { previousVersion: raw.previousVersion } : {}),
      ...(raw.step !== undefined ? { step: raw.step } : {}),
      ...(raw.error !== undefined ? { error: raw.error } : {}),
      failedCount: typeof raw.failedCount === 'number' ? raw.failedCount : 0,
      updatedAt: typeof raw.updatedAt === 'string' ? raw.updatedAt : new Date().toISOString(),
    };
  } catch {
    return null;
  }
}

/** Write via tmp + rename so a crash mid-write never leaves a half file behind. */
export function writeUpdateState(file: string, state: UpdateState, now: () => Date = () => new Date()): void {
  const payload: UpdateState = { ...state, updatedAt: now().toISOString() };
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const tmp = `${file}.tmp`;
  fs.writeFileSync(tmp, `${JSON.stringify(payload, null, 2)}\n`);
  fs.renameSync(tmp, file);
}
