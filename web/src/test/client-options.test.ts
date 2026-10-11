import { describe, expect, it } from 'vitest';
import type { ClientOptionDescriptor } from '../api/types';
import { clientOptionsKey, clientOptionsOf } from '../lib/clientOptions';

const descriptors: ClientOptionDescriptor[] = [
  { key: 'context1m', type: 'bool', default: 'false', scope: 'config' },
  { key: 'cacheTtl', type: 'enum', default: 'default', scope: 'gateway', choices: ['default', '5m', '1h'] },
  { key: 'maxThinkingTokens', type: 'int', default: '', scope: 'config' },
];

describe('clientOptionsOf', () => {
  it('keeps only known, non-default values', () => {
    const extras = { options: { context1m: true, cacheTtl: 'default', maxThinkingTokens: 0, nope: true } };
    expect(clientOptionsOf(extras, descriptors)).toEqual({ context1m: true });
  });

  it('rejects values outside the descriptor', () => {
    expect(clientOptionsOf({ options: { cacheTtl: '2h', maxThinkingTokens: 'x' } }, descriptors)).toEqual({});
    expect(clientOptionsOf({ options: { cacheTtl: '1h', maxThinkingTokens: 8000 } }, descriptors)).toEqual({ cacheTtl: '1h', maxThinkingTokens: 8000 });
  });

  it('gives an order-independent comparison key', () => {
    expect(clientOptionsKey({ b: 1, a: true })).toBe(clientOptionsKey({ a: true, b: 1 }));
  });
});
