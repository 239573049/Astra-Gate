import { describe, expect, it } from 'vitest';
import { autoProtocol, protocolModelsOf, protocolOverridesKey, protocolOverridesOf } from '../lib/clientProtocols';

describe('protocolOverridesOf', () => {
  it('keeps only known protocols', () => {
    const extras = { protocolOverrides: { a: 'openai-responses', b: 'smoke-signals', c: 3, d: 'anthropic' } };
    expect(protocolOverridesOf(extras)).toEqual({ a: 'openai-responses', d: 'anthropic' });
    expect(protocolOverridesOf({})).toEqual({});
    expect(protocolOverridesOf(null)).toEqual({});
  });

  it('has an order-independent key', () => {
    expect(protocolOverridesKey({ b: 'anthropic', a: 'openai-chat' })).toBe(protocolOverridesKey({ a: 'openai-chat', b: 'anthropic' }));
  });
});

describe('protocolModelsOf', () => {
  it('reads the model hints in order, each id once', () => {
    const extras = {
      models: {
        'gpt-5.5': { id: 'gpt-5.5', name: 'GPT-5.5', protocols: ['openai-responses', 'nope'] },
        alias: { id: 'claude', protocols: ['anthropic'] },
        dup: { id: 'gpt-5.5', name: 'again' },
        broken: { name: 'no id' },
      },
    };
    expect(protocolModelsOf(extras)).toEqual([
      { id: 'gpt-5.5', name: 'GPT-5.5', native: ['openai-responses'] },
      { id: 'claude', name: 'claude', native: ['anthropic'] },
    ]);
  });
});

describe('autoProtocol', () => {
  it('takes the first native protocol the client speaks, else its default (translated)', () => {
    const supported = ['openai-chat', 'openai-responses', 'anthropic'] as const;
    expect(autoProtocol({ id: 'm', name: 'm', native: ['gemini', 'anthropic'] }, [...supported])).toEqual({ protocol: 'anthropic', translated: false });
    expect(autoProtocol({ id: 'm', name: 'm', native: ['gemini'] }, [...supported])).toEqual({ protocol: 'openai-chat', translated: true });
    expect(autoProtocol({ id: 'm', name: 'm', native: [] }, [...supported])).toEqual({ protocol: 'openai-chat', translated: true });
  });
});
