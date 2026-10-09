/**
 * A provider's model mapping (settings.model_map): requested model id → one of the provider's model ids. The gateway
 * sends the mapped id upstream, and a client bound to several providers routes the requested id to this provider.
 */
export type ModelMap = Record<string, string>;

/** The non-empty string entries of a provider's settings.model_map. */
export function modelMapOf(settings: Record<string, unknown> | null | undefined): ModelMap {
  const raw = settings?.model_map;
  const out: ModelMap = {};
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return out;
  for (const [from, to] of Object.entries(raw as Record<string, unknown>)) if (from && typeof to === 'string' && to) out[from] = to;
  return out;
}
