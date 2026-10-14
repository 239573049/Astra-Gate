import type { ApiProtocol } from '../api/types';

const PROTOCOLS: readonly ApiProtocol[] = ['openai-chat', 'openai-responses', 'anthropic', 'gemini'];

const isProtocol = (v: unknown): v is ApiProtocol => typeof v === 'string' && (PROTOCOLS as readonly string[]).includes(v);

/** The user's per-model protocol choices (extras.protocolOverrides); unknown values are dropped. */
export function protocolOverridesOf(extras: Record<string, unknown> | null | undefined): Record<string, ApiProtocol> {
  const raw = extras?.protocolOverrides;
  const out: Record<string, ApiProtocol> = {};
  if (!raw || typeof raw !== 'object') return out;
  for (const [model, value] of Object.entries(raw as Record<string, unknown>)) if (isProtocol(value)) out[model] = value;
  return out;
}

/** One model the client lists (extras.models), with the protocols the gateway passes it through in, preferred first. */
export interface ProtocolModel {
  id: string;
  name: string;
  native: ApiProtocol[];
}

/** The models of a client's extras.models, in order. */
export function protocolModelsOf(extras: Record<string, unknown> | null | undefined): ProtocolModel[] {
  const raw = extras?.models;
  if (!raw || typeof raw !== 'object') return [];
  const out: ProtocolModel[] = [];
  for (const value of Object.values(raw as Record<string, unknown>)) {
    if (!value || typeof value !== 'object') continue;
    const m = value as Record<string, unknown>;
    if (typeof m.id !== 'string' || !m.id || out.some((x) => x.id === m.id)) continue;
    out.push({
      id: m.id,
      name: typeof m.name === 'string' && m.name ? m.name : m.id,
      native: Array.isArray(m.protocols) ? m.protocols.filter(isProtocol) : [],
    });
  }
  return out;
}

/**
 * The protocol a model gets without a choice of its own (the server's ClientAdapterBase.ModelProtocols): its first native
 * protocol the client speaks; otherwise the client's default, which the gateway translates.
 */
export function autoProtocol(model: ProtocolModel, supported: ApiProtocol[]): { protocol: ApiProtocol; translated: boolean } {
  const native = model.native.find((p) => supported.includes(p));
  return native ? { protocol: native, translated: false } : { protocol: supported[0], translated: true };
}

/** Stable comparison key of an override map. */
export const protocolOverridesKey = (o: Record<string, ApiProtocol>) =>
  Object.keys(o)
    .sort()
    .map((k) => `${k}=${o[k]}`)
    .join('\n');
