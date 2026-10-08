import { describe, expect, it } from 'vitest';

import { modelSlotsOf } from '../lib/modelSlots';
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

describe('modelSlotsOf (Claude Code model slots)', () => {
  it('keeps known, non-empty slots and trims them', () => {
    expect(modelSlotsOf({ models: { ANTHROPIC_DEFAULT_HAIKU_MODEL: ' glm-4.5-air ', ANTHROPIC_MODEL: 'glm-4.6', ANTHROPIC_DEFAULT_OPUS_MODEL: '', other: 'x' } })).toEqual({
      ANTHROPIC_MODEL: 'glm-4.6',
      ANTHROPIC_DEFAULT_HAIKU_MODEL: 'glm-4.5-air',
    });
  });

  it('tolerates missing or malformed extras', () => {
    expect(modelSlotsOf(undefined)).toEqual({});
    expect(modelSlotsOf({})).toEqual({});
    expect(modelSlotsOf({ models: 'ANTHROPIC_MODEL' })).toEqual({});
    expect(modelSlotsOf({ models: { ANTHROPIC_MODEL: 42 } })).toEqual({});
  });
});
