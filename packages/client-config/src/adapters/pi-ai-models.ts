import { ClientAdapterBase } from '../adapter-base.js';
import type { JsonObject } from '../contracts.js';
import type { ApiProtocol } from '../domain/protocols.js';

/**
 * The pi-ai model vocabulary shared by Pi, omp, MiniMax Code and DeepSeek Harness: the `api` names of the wire
 * protocols and the gateway base each protocol's SDK expects. The OpenAI SDKs append `/chat/completions` or
 * `/responses` to `…/v1`; pi-ai's Anthropic client appends `/v1/messages` itself, so it gets the gateway
 * root; its Gemini client posts to `{base}/models/…` with the version inside the base, so it gets `…/v1beta`.
 * (Port of PiAiModels.)
 */
export const PiAiModels = {
  apiOf(protocol: ApiProtocol): string {
    switch (protocol) {
      case 'openai-responses':
        return 'openai-responses';
      case 'anthropic':
        return 'anthropic-messages';
      case 'gemini':
        return 'google-generative-ai';
      default:
        return 'openai-completions';
    }
  },

  /** The base URL pi-ai's client for `protocol` needs to reach the gateway. */
  baseUrlFor(gatewayBaseUrl: string, protocol: ApiProtocol): string {
    const root = gatewayBaseUrl.replace(/\/+$/, '');
    switch (protocol) {
      case 'anthropic':
        return root;
      case 'gemini':
        return root + '/v1beta';
      default:
        return root + '/v1';
    }
  },

  /**
   * The per-model fields Astra knows, under pi-ai's names: `reasoning`, `input` (text, plus image for vision
   * models), `contextWindow` and `maxTokens`; unknown ones are left to the client's own defaults or catalog.
   */
  addHints(model: Record<string, unknown>, info: JsonObject | undefined): void {
    const reasoning = ClientAdapterBase.boolHint(info?.reasoning);
    if (reasoning !== null) model.reasoning = reasoning;
    const vision = ClientAdapterBase.boolHint(info?.vision);
    if (vision !== null) model.input = vision ? ['text', 'image'] : ['text'];
    const context = ClientAdapterBase.positiveLong(info?.contextWindow);
    if (context !== null) model.contextWindow = context;
    const maxTokens = ClientAdapterBase.positiveLong(info?.maxOutputTokens);
    if (maxTokens !== null) model.maxTokens = maxTokens;
  },
};
