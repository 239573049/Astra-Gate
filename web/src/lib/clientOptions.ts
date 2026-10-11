import type { ClientOptionDescriptor } from '../api/types';

/** A client's option values as stored in extras.options: booleans, positive integers, or enum strings. */
export type ClientOptionValues = Record<string, boolean | number | string>;

/** The known, non-default options of a client's extras.options. */
export function clientOptionsOf(extras: Record<string, unknown> | null | undefined, descriptors: ClientOptionDescriptor[] | null | undefined): ClientOptionValues {
  const raw = extras?.options;
  const out: ClientOptionValues = {};
  if (!raw || typeof raw !== 'object') return out;
  for (const d of descriptors ?? []) {
    const v = (raw as Record<string, unknown>)[d.key];
    if (d.type === 'bool' && v === true) out[d.key] = true;
    else if (d.type === 'int' && typeof v === 'number' && v > 0) out[d.key] = v;
    else if (d.type === 'enum' && typeof v === 'string' && v !== 'default' && d.choices?.includes(v)) out[d.key] = v;
  }
  return out;
}

/** Stable comparison key of an option map. */
export const clientOptionsKey = (o: ClientOptionValues) =>
  Object.keys(o)
    .sort()
    .map((k) => `${k}=${o[k]}`)
    .join('\n');
