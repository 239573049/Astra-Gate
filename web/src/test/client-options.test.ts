import { describe, expect, it } from 'vitest';
import type { ClientOptionDescriptor } from '../api/types';
import {
  clientOptionsKey,
  clientOptionsOf,
  formatIntOptionChoice,
  intOptionChoices,
} from '../lib/clientOptions';

const descriptors: ClientOptionDescriptor[] = [
  { key: 'context1m', type: 'bool', default: 'false', scope: 'config' },
  {
    key: 'cacheTtl',
    type: 'enum',
    default: 'default',
    scope: 'gateway',
    choices: ['default', '5m', '1h'],
  },
  { key: 'maxThinkingTokens', type: 'int', default: '', scope: 'config' },
  {
    key: 'contextWindow',
    type: 'int',
    default: '',
    scope: 'config',
    choices: ['128000', '400000', '1000000'],
  },
];

describe('clientOptionsOf', () => {
  it('keeps only known, non-default values', () => {
    const extras = {
      options: { context1m: true, cacheTtl: 'default', maxThinkingTokens: 0, nope: true },
    };
    expect(clientOptionsOf(extras, descriptors)).toEqual({ context1m: true });
  });

  it('rejects values outside the descriptor', () => {
    expect(
      clientOptionsOf({ options: { cacheTtl: '2h', maxThinkingTokens: 'x' } }, descriptors),
    ).toEqual({});
    expect(
      clientOptionsOf({ options: { cacheTtl: '1h', maxThinkingTokens: 8000 } }, descriptors),
    ).toEqual({ cacheTtl: '1h', maxThinkingTokens: 8000 });
  });

  it('gives an order-independent comparison key', () => {
    expect(clientOptionsKey({ b: 1, a: true })).toBe(clientOptionsKey({ a: true, b: 1 }));
  });

  it('formats preset integer choices and keeps legacy custom values in order', () => {
    expect(formatIntOptionChoice(128000)).toBe('128K');
    expect(formatIntOptionChoice(1000000)).toBe('1M');
    expect(formatIntOptionChoice(131072)).toBe('131,072');
    expect(intOptionChoices(descriptors[3]!, 300000, 'Not set')).toEqual([
      { value: '', label: 'Not set' },
      { value: '128000', label: '128K' },
      { value: '300000', label: '300K' },
      { value: '400000', label: '400K' },
      { value: '1000000', label: '1M' },
    ]);
  });
});
