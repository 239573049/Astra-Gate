import { describe, expect, it } from 'vitest';

import { rolesOf } from '../lib/roleMap';

describe('rolesOf (Claude Desktop role map)', () => {
  it('keeps known, non-empty roles and trims them', () => {
    expect(rolesOf({ roleMap: { sonnet: ' glm-4.6 ', opus: '', haiku: 'glm-4.5-air', extra: 'x' } })).toEqual({
      sonnet: 'glm-4.6',
      haiku: 'glm-4.5-air',
    });
  });

  it('tolerates missing or malformed extras', () => {
    expect(rolesOf(undefined)).toEqual({});
    expect(rolesOf({})).toEqual({});
    expect(rolesOf({ roleMap: 'sonnet' })).toEqual({});
    expect(rolesOf({ roleMap: { sonnet: 42 } })).toEqual({});
  });
});
