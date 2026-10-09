import type { ClientInfo, ClientKind } from '../api/types';

/** A client counts as present when it was detected, its install probe found it, or Astra manages its config. */
export function isClientInstalled(c: ClientInfo): boolean {
  return Boolean(c.detection?.installed || c.install?.installed || c.enabled);
}

/**
 * The clients the overview filter offers: installed ones in `order`, plus the currently selected client (even when it is
 * no longer installed, so the active filter never disappears from the control). Empty while the list is still loading.
 */
export function filterableClients(clients: ClientInfo[] | undefined, order: readonly ClientKind[], selected?: ClientKind | 'all'): ClientKind[] {
  const present = new Set((clients ?? []).filter(isClientInstalled).map((c) => c.kind));
  return order.filter((k) => present.has(k) || k === selected);
}
