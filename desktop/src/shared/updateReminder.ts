/**
 * Update reminder policy (circuit breaker). Pure logic, no Electron imports, so it stays
 * unit-testable (see tests/updateReminder.test.ts).
 *
 * The desktop app checks for updates on every launch and the user should hear about a pending
 * update, but never be nagged. For one version the user is reminded at most
 * MAX_REMINDERS_PER_VERSION times, at least REMINDER_COOLDOWN_MS apart, and never again once they
 * chose "skip this version". A newer version starts a fresh budget. The state is persisted across
 * launches (desktop-prefs.json) — without that, a launch-time check would remind on every start.
 */

export const REMINDER_COOLDOWN_MS = 24 * 60 * 60 * 1000;
export const MAX_REMINDERS_PER_VERSION = 3;

export interface UpdateReminderState {
  /** Version the last reminder was about. */
  version: string | null;
  /** How many reminders were shown for `version`. */
  count: number;
  /** Epoch ms of the last reminder (recorded before it is shown, so a crash mid-dialog still counts). */
  lastRemindedAt: number | null;
  /** Version the user chose to skip; reminders for exactly this version stay off. */
  skippedVersion: string | null;
}

export const EMPTY_REMINDER_STATE: UpdateReminderState = {
  version: null,
  count: 0,
  lastRemindedAt: null,
  skippedVersion: null,
};

export type ReminderDecision = { remind: true } | { remind: false; reason: 'skipped' | 'cooldown' | 'limit' };

export interface ReminderPolicy {
  cooldownMs?: number;
  maxPerVersion?: number;
}

export function decideReminder(
  state: UpdateReminderState,
  version: string,
  now: number,
  policy: ReminderPolicy = {},
): ReminderDecision {
  if (state.skippedVersion === version) return { remind: false, reason: 'skipped' };
  if (state.version !== version) return { remind: true };
  if (state.count >= (policy.maxPerVersion ?? MAX_REMINDERS_PER_VERSION)) return { remind: false, reason: 'limit' };
  // A clock moved backwards gives a negative elapsed time, which also counts as "still cooling down".
  if (state.lastRemindedAt !== null && now - state.lastRemindedAt < (policy.cooldownMs ?? REMINDER_COOLDOWN_MS)) {
    return { remind: false, reason: 'cooldown' };
  }
  return { remind: true };
}

/** State after a reminder for `version` is shown at `now`. */
export function recordReminder(state: UpdateReminderState, version: string, now: number): UpdateReminderState {
  return {
    ...state,
    version,
    count: state.version === version ? state.count + 1 : 1,
    lastRemindedAt: now,
  };
}

export function skipVersion(state: UpdateReminderState, version: string): UpdateReminderState {
  return { ...state, skippedVersion: version };
}

/** Tolerant parser: malformed or missing fields fall back to the empty state one by one. */
export function parseReminderState(raw: unknown): UpdateReminderState {
  const o = typeof raw === 'object' && raw !== null ? (raw as Record<string, unknown>) : {};
  const str = (v: unknown): string | null => (typeof v === 'string' && v !== '' ? v : null);
  const num = (v: unknown): number | null => (typeof v === 'number' && Number.isFinite(v) ? v : null);
  const count = num(o.count);
  return {
    version: str(o.version),
    count: count !== null && count >= 0 ? Math.floor(count) : 0,
    lastRemindedAt: num(o.lastRemindedAt),
    skippedVersion: str(o.skippedVersion),
  };
}

/** Reminder texts; Chinese when the OS locale is zh-*. */
export function reminderLabels(locale: string) {
  const zh = locale.toLowerCase().startsWith('zh');
  return {
    title: zh ? '发现新版本' : 'Update available',
    message: (version: string) => (zh ? `Astra ${version} 现已可用` : `Astra ${version} is available`),
    update: zh ? '立即更新' : 'Update Now',
    later: zh ? '稍后提醒' : 'Remind Me Later',
    skip: zh ? '跳过此版本' : 'Skip This Version',
    notificationBody: zh ? '点击查看并更新' : 'Click to review and update',
    updated: zh ? 'Astra 已更新' : 'Astra has been updated',
    failed: zh ? '更新失败' : 'Update failed',
  };
}
