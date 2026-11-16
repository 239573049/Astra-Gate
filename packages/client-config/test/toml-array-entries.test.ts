import { describe, expect, it } from 'vitest';
import { hasArrayEntries, removeArrayEntries, setArrayEntries } from '../src/editing/toml-array-entries.js';

const text =
  '[[models]]\nprovider = "mine"\nmodel_id = "a"\n\n[x]\nk = 1\n\n[[models]]\nprovider = "astra"\nmodel_id = "old"\n';
const selector = 'models[provider=astra]';

describe('TOML array entries', () => {
  it('replaces and removes only the matching entries', () => {
    expect(hasArrayEntries(text, selector)).toBe(true);
    const replaced = setArrayEntries(text, selector, [
      [
        ['provider', '"astra"'],
        ['model_id', '"new"'],
      ],
    ]);
    expect(replaced).toContain('model_id = "new"');
    expect(replaced).not.toContain('model_id = "old"');
    expect(replaced).toContain('model_id = "a"');
    const removed = removeArrayEntries(replaced, selector);
    expect(hasArrayEntries(removed, selector)).toBe(false);
    expect(removed).toContain('model_id = "a"');
  });

  it('refuses a result that is not valid TOML', () => {
    expect(() =>
      setArrayEntries('a = 1\n', selector, [
        [
          ['provider', '"astra"'],
          ['broken', '[1, '],
        ],
      ]),
    ).toThrow();
  });
});
