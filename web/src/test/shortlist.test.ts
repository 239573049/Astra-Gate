import { describe, expect, it } from 'vitest';

import { shortlistOf } from '../components/ClientShortlistGroup';

describe('shortlistOf', () => {
  it('reads the ids a client was limited to, in order', () => {
    expect(shortlistOf({ modelShortlist: ['b', 'a'] })).toEqual(['b', 'a']);
  });

  it('is empty when none is set or the value is not a list', () => {
    expect(shortlistOf(undefined)).toEqual([]);
    expect(shortlistOf({})).toEqual([]);
    expect(shortlistOf({ modelShortlist: 'x' })).toEqual([]);
    expect(shortlistOf({ modelShortlist: ['a', 3, null] })).toEqual(['a']);
  });
});
