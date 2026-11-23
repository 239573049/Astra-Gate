import type { ClientOptionDescriptor } from '../api/types';

/** A client's option values as stored in extras.options: booleans, positive integers, or enum strings. */
export type ClientOptionValues = Record<string, boolean | number | string>;

/** The known, non-default options of a client's extras.options. */
export function clientOptionsOf(
  extras: Record<string, unknown> | null | undefined,
  descriptors: ClientOptionDescriptor[] | null | undefined,
): ClientOptionValues {
  const raw = extras?.options;
  const out: ClientOptionValues = {};
  if (!raw || typeof raw !== 'object') return out;
  for (const d of descriptors ?? []) {
    const v = (raw as Record<string, unknown>)[d.key];
    if (d.type === 'bool' && v === true) out[d.key] = true;
    else if (d.type === 'int' && typeof v === 'number' && v > 0) out[d.key] = v;
    else if (
      d.type === 'enum' &&
      typeof v === 'string' &&
      v !== 'default' &&
      d.choices?.includes(v)
    )
      out[d.key] = v;
  }
  return out;
}

/** Stable comparison key of an option map. */
export const clientOptionsKey = (o: ClientOptionValues) =>
  Object.keys(o)
    .sort()
    .map((k) => `${k}=${o[k]}`)
    .join('\n');

/** Formats a positive integer preset for display (e.g. 200000 -> "200K", 1000000 -> "1M"). */
export function formatIntOptionChoice(n: number): string {
  if (n >= 1_000_000 && n % 1_000_000 === 0) return `${n / 1_000_000}M`;
  if (n >= 1_000 && n % 1_000 === 0) return `${n / 1_000}K`;
  return n.toLocaleString('en-US');
}

/** Dropdown options for an int descriptor with preset choices (keeps any legacy custom value selectable). */
export function intOptionChoices(
  d: ClientOptionDescriptor,
  current: number | undefined,
  unsetLabel: string,
): { value: string; label: string }[] {
  const nums: number[] = [];
  for (const raw of d.choices ?? []) {
    const n = Number.parseInt(raw, 10);
    if (Number.isFinite(n) && n > 0 && !nums.includes(n)) nums.push(n);
  }
  if (typeof current === 'number' && current > 0 && !nums.includes(current)) {
    nums.push(current);
    nums.sort((a, b) => a - b);
  }
  return [
    { value: '', label: unsetLabel },
    ...nums.map((n) => ({ value: String(n), label: formatIntOptionChoice(n) })),
  ];
}
