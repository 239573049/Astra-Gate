import { describe, expect, it } from 'vitest';

import {
  EMPTY_REMINDER_STATE,
  MAX_REMINDERS_PER_VERSION,
  REMINDER_COOLDOWN_MS,
  decideReminder,
  parseReminderState,
  recordReminder,
  skipVersion,
  type UpdateReminderState,
} from '../src/shared/updateReminder';

const T0 = 1_800_000_000_000;

describe('decideReminder', () => {
  it('reminds for a version never reminded about', () => {
    expect(decideReminder(EMPTY_REMINDER_STATE, '0.5.0', T0)).toEqual({ remind: true });
  });

  it('stays quiet during the cooldown after a reminder', () => {
    const state = recordReminder(EMPTY_REMINDER_STATE, '0.5.0', T0);
    expect(decideReminder(state, '0.5.0', T0 + 1)).toEqual({ remind: false, reason: 'cooldown' });
    expect(decideReminder(state, '0.5.0', T0 + REMINDER_COOLDOWN_MS - 1)).toEqual({ remind: false, reason: 'cooldown' });
    expect(decideReminder(state, '0.5.0', T0 + REMINDER_COOLDOWN_MS)).toEqual({ remind: true });
  });

  it('stops for good after the per-version cap, however much time passes', () => {
    let state: UpdateReminderState = EMPTY_REMINDER_STATE;
    let now = T0;
    for (let i = 0; i < MAX_REMINDERS_PER_VERSION; i++) {
      expect(decideReminder(state, '0.5.0', now)).toEqual({ remind: true });
      state = recordReminder(state, '0.5.0', now);
      now += REMINDER_COOLDOWN_MS;
    }
    expect(state.count).toBe(MAX_REMINDERS_PER_VERSION);
    expect(decideReminder(state, '0.5.0', now + 365 * REMINDER_COOLDOWN_MS)).toEqual({ remind: false, reason: 'limit' });
  });

  it('a newer version gets a fresh budget, even right after the previous reminder', () => {
    const state = recordReminder(EMPTY_REMINDER_STATE, '0.5.0', T0);
    expect(decideReminder(state, '0.6.0', T0 + 1)).toEqual({ remind: true });
    expect(recordReminder(state, '0.6.0', T0 + 1)).toMatchObject({ version: '0.6.0', count: 1 });
  });

  it('never reminds about a skipped version, but does for the next one', () => {
    const state = skipVersion(recordReminder(EMPTY_REMINDER_STATE, '0.5.0', T0), '0.5.0');
    expect(decideReminder(state, '0.5.0', T0 + 30 * REMINDER_COOLDOWN_MS)).toEqual({ remind: false, reason: 'skipped' });
    expect(decideReminder(state, '0.5.1', T0 + 1)).toEqual({ remind: true });
  });

  it('treats a clock moved backwards as still cooling down', () => {
    const state = recordReminder(EMPTY_REMINDER_STATE, '0.5.0', T0);
    expect(decideReminder(state, '0.5.0', T0 - 3 * REMINDER_COOLDOWN_MS)).toEqual({ remind: false, reason: 'cooldown' });
  });

  it('honours a custom policy', () => {
    const state = recordReminder(EMPTY_REMINDER_STATE, '0.5.0', T0);
    expect(decideReminder(state, '0.5.0', T0 + 10, { cooldownMs: 5 })).toEqual({ remind: true });
    expect(decideReminder(state, '0.5.0', T0 + 10, { cooldownMs: 5, maxPerVersion: 1 })).toEqual({
      remind: false,
      reason: 'limit',
    });
  });
});

describe('parseReminderState', () => {
  it('falls back to the empty state on garbage and keeps valid fields one by one', () => {
    expect(parseReminderState(null)).toEqual(EMPTY_REMINDER_STATE);
    expect(parseReminderState('x')).toEqual(EMPTY_REMINDER_STATE);
    expect(parseReminderState({ version: 5, count: -1, lastRemindedAt: 'now', skippedVersion: '' })).toEqual(
      EMPTY_REMINDER_STATE,
    );
    expect(parseReminderState({ version: '0.5.0', count: 2.9, lastRemindedAt: T0, skippedVersion: '0.4.9' })).toEqual({
      version: '0.5.0',
      count: 2,
      lastRemindedAt: T0,
      skippedVersion: '0.4.9',
    });
  });
});
