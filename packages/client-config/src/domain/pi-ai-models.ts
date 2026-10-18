import { ClientAdapterBase } from '../adapter-base.js';
import type { JsonObject } from '../contracts.js';
import type { ApiProtocol } from './protocols.js';

/**
 * The pi-ai model vocabulary shared by Pi, omp, MiniMax Code and DeepSeek Harness: the `api` names of the wire
 * protocols and the gateway base each protocol's SDK expects (Port of PiAiModels).
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

  /** The per-model fields Astra knows, under pi-ai's names. */
  addHints(model: JsonObject, info: JsonObject | null | undefined): void {
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
