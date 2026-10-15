import { describe, expect, it } from 'vitest';

import { modelSlotsOf } from '../lib/modelSlots';
import { rolesOf, suggestRoles } from '../lib/roleMap';

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

describe('suggestRoles (Claude Desktop role map prefill)', () => {
  it('puts the full-size model on sonnet and opus and the small one on haiku', () => {
    expect(suggestRoles(['deepseek-flash', 'deepseek-v4-pro'])).toEqual({
      sonnet: 'deepseek-v4-pro',
      opus: 'deepseek-v4-pro',
      haiku: 'deepseek-flash',
    });
    expect(suggestRoles(['glm-4.5-air', 'glm-4.6'])).toEqual({ sonnet: 'glm-4.6', opus: 'glm-4.6', haiku: 'glm-4.5-air' });
  });

  it('maps Claude models to their own role', () => {
    expect(suggestRoles(['claude-haiku-4-5', 'claude-opus-5-5', 'claude-sonnet-5-5'])).toEqual({
      sonnet: 'claude-sonnet-5-5',
      opus: 'claude-opus-5-5',
      haiku: 'claude-haiku-4-5',
    });
  });

  it('uses the one model everywhere, and nothing without models', () => {
    expect(suggestRoles(['gpt-4o-mini'])).toEqual({ sonnet: 'gpt-4o-mini', opus: 'gpt-4o-mini', haiku: 'gpt-4o-mini' });
    // "gemini" is not the "mini" tier: only whole name parts count.
    expect(suggestRoles(['gemini-2.5-pro'])).toEqual({ sonnet: 'gemini-2.5-pro', opus: 'gemini-2.5-pro', haiku: 'gemini-2.5-pro' });
    expect(suggestRoles([])).toEqual({});
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
