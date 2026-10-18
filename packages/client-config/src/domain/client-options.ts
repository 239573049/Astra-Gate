import type { JsonObject } from '../contracts.js';
import { ClientKinds } from './kinds.js';

/** One client-specific option (Astra.Core ClientOptionDescriptor). */
export interface ClientOptionDescriptor {
  key: string;
  type: 'bool' | 'int' | 'enum';
  default: string;
  scope: 'config' | 'gateway';
  choices?: string[];
}

export const CLIENT_OPTIONS_KEY = 'options';

export const ClientOptionKeys = {
  context1M: 'context1m',
  cacheTtl: 'cacheTtl',
  maxThinkingTokens: 'maxThinkingTokens',
  disablePromptCaching: 'disablePromptCaching',
  contextWindow: 'contextWindow',
  autoCompactTokenLimit: 'autoCompactTokenLimit',
  reasoningEffort: 'reasoningEffort',
  modelCatalog: 'modelCatalog',
} as const;

const CLAUDE_CODE_OPTIONS: ClientOptionDescriptor[] = [
  { key: 'context1m', type: 'bool', default: 'false', scope: 'config' },
  { key: 'cacheTtl', type: 'enum', default: 'default', scope: 'gateway', choices: ['default', '5m', '1h'] },
  { key: 'maxThinkingTokens', type: 'int', default: '', scope: 'config' },
  { key: 'disablePromptCaching', type: 'bool', default: 'false', scope: 'config' },
];

const CODEX_OPTIONS: ClientOptionDescriptor[] = [
  { key: 'contextWindow', type: 'int', default: '', scope: 'config' },
  { key: 'autoCompactTokenLimit', type: 'int', default: '', scope: 'config' },
  {
    key: 'reasoningEffort',
    type: 'enum',
    default: 'default',
    scope: 'config',
    choices: ['default', 'minimal', 'low', 'medium', 'high'],
  },
  { key: 'modelCatalog', type: 'bool', default: 'false', scope: 'config' },
];

/** The options a client supports; empty for clients without any. */
export function clientOptionsFor(kind: string): ClientOptionDescriptor[] {
  switch (kind) {
    case ClientKinds.ClaudeCode:
      return CLAUDE_CODE_OPTIONS;
    case ClientKinds.Codex:
      return CODEX_OPTIONS;
    default:
      return [];
  }
}

/** Config key paths a client's "config" options can write, so unsetting an option restores what Astra replaced. */
export function clientOptionConfigKeyPaths(kind: string): string[] {
  switch (kind) {
    case ClientKinds.ClaudeCode:
      return ['env.MAX_THINKING_TOKENS', 'env.DISABLE_PROMPT_CACHING'];
    case ClientKinds.Codex:
      // "models" is the key of the Astra-owned model catalog file (CodexClientAdapter), not of config.toml.
      return [
        'model_context_window',
        'model_auto_compact_token_limit',
        'model_reasoning_effort',
        'model_catalog_json',
        'models',
      ];
    default:
      return [];
  }
}

const optionsOf = (extras: JsonObject | null | undefined): JsonObject | null => {
  const o = extras?.[CLIENT_OPTIONS_KEY];
  return o !== null && typeof o === 'object' && !Array.isArray(o) ? (o as JsonObject) : null;
};

export const getBoolOption = (extras: JsonObject | null | undefined, key: string): boolean =>
  optionsOf(extras)?.[key] === true;

/** A positive integer option, or null when unset / zero / not an integer. */
export function getIntOption(extras: JsonObject | null | undefined, key: string): number | null {
  const v = optionsOf(extras)?.[key];
  return typeof v === 'number' && Number.isInteger(v) && v > 0 ? v : null;
}

/** An enum / string option; null when unset, blank or "default". */
export function getChoiceOption(extras: JsonObject | null | undefined, key: string): string | null {
  const v = optionsOf(extras)?.[key];
  return typeof v === 'string' && v.trim() !== '' && v !== 'default' ? v.trim() : null;
}

/** Validates an "options" object against the client's descriptors; returns an error message, or null. */
export function validateClientOptions(kind: string, options: JsonObject): string | null {
  const descriptors = clientOptionsFor(kind);
  for (const [key, value] of Object.entries(options)) {
    const d = descriptors.find((x) => x.key === key);
    if (!d) return `Unknown option '${key}' for ${kind}`;
    if (value === null) continue;
    const ok =
      d.type === 'bool'
        ? typeof value === 'boolean'
        : d.type === 'int'
          ? typeof value === 'number' && Number.isInteger(value) && value >= 0
          : typeof value === 'string' && (d.choices?.includes(value) ?? false);
    if (!ok) return `Invalid value for option '${key}'`;
  }
  return null;
}
