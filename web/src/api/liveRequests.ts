import { useQueryClient } from '@tanstack/react-query';
import { useEffect, useSyncExternalStore } from 'react';

import { apiEventStream, type SseMessage } from './client';
import type { RequestLiveEvent, RequestSummary } from './types';

type Listener = () => void;

/** Fields the request list filters on; a change to any of them can move a row in or out of the filtered list. */
const listKey = (r: RequestSummary) =>
  [r.clientKind, r.tokenId, r.providerId, r.status, r.requestedModel, r.upstreamModel, r.systemModelId, r.responseModel].join('\u0000');

/**
 * Rows of the live request log (GET /api/requests/live), outside React state so an update re-renders only what
 * shows that row: {@link useLiveRequestList} changes when a row joins or leaves the feed (or a field the list filters
 * on changes), {@link useLiveRequest} changes for that one row's every update (TTFT, status, cost…).
 */
class LiveRequestStore {
  private readonly rows = new Map<string, RequestSummary>();
  private list: readonly RequestSummary[] = [];
  private connected = false;
  private readonly listListeners = new Set<Listener>();
  private readonly rowListeners = new Map<string, Set<Listener>>();

  readonly getList = () => this.list;
  readonly getConnected = () => this.connected;
  readonly get = (id: string) => this.rows.get(id);

  readonly subscribeList = (listener: Listener) => {
    this.listListeners.add(listener);
    return () => void this.listListeners.delete(listener);
  };

  subscribeRow(id: string, listener: Listener) {
    let set = this.rowListeners.get(id);
    if (!set) this.rowListeners.set(id, (set = new Set()));
    set.add(listener);
    return () => {
      set.delete(listener);
      if (set.size === 0) this.rowListeners.delete(id);
    };
  }

  set(row: RequestSummary) {
    const prev = this.rows.get(row.id);
    this.rows.set(row.id, row);
    this.notifyRow(row.id);
    if (!prev || listKey(prev) !== listKey(row)) this.rebuildList();
  }

  delete(id: string) {
    if (!this.rows.delete(id)) return;
    this.notifyRow(id);
    this.rebuildList();
  }

  reset(connected: boolean) {
    const ids = [...this.rows.keys()];
    this.rows.clear();
    for (const id of ids) this.notifyRow(id);
    const changed = this.connected !== connected || ids.length > 0;
    this.connected = connected;
    if (changed) this.rebuildList();
  }

  setConnected(connected: boolean) {
    if (this.connected === connected) return;
    this.connected = connected;
    this.rebuildList();
  }

  private rebuildList() {
    // Newest first; ULID ids sort by arrival, so ties on the timestamp stay stable.
    this.list = [...this.rows.values()].sort((a, b) => (a.startedAtUtc === b.startedAtUtc ? (a.id < b.id ? 1 : -1) : a.startedAtUtc < b.startedAtUtc ? 1 : -1));
    for (const listener of this.listListeners) listener();
  }

  private notifyRow(id: string) {
    const set = this.rowListeners.get(id);
    if (set) for (const listener of set) listener();
  }
}

const store = new LiveRequestStore();
let consumers = 0;
let stop: (() => void) | undefined;

/** Opens the live stream while at least one consumer wants it; reconnects with backoff while it is down. */
function connect(onPersisted: (id: string) => void, onReconnect: () => void): () => void {
  const controller = new AbortController();
  const onEvent = (message: SseMessage) => {
    const e = message.data as RequestLiveEvent | null;
    if (!e || typeof e !== 'object' || !e.request?.id) return;
    if (e.type === 'persisted') {
      onPersisted(e.request.id);
      // The stored row replaces this one once the list refetch lands; until then the cells keep showing it.
      setTimeout(() => store.delete(e.request.id), 1500);
    } else store.set(e.request);
  };

  void (async () => {
    let delay = 1000;
    while (!controller.signal.aborted) {
      try {
        await apiEventStream('/api/requests/live', onEvent, controller.signal, () => store.setConnected(true));
        delay = 1000;
      } catch {
        // Server restarting or unreachable: retry below.
      }
      if (controller.signal.aborted) return;
      // Rows of the dropped connection may never see their "persisted" event: forget them (the snapshot on reconnect
      // re-sends whatever is still in flight) and let the list catch up with what was written meanwhile.
      store.reset(false);
      onReconnect();
      await new Promise((resolve) => setTimeout(resolve, delay));
      delay = Math.min(delay * 2, 15000);
    }
  })();

  return () => {
    controller.abort();
    store.reset(false);
  };
}

/**
 * Keeps the live request stream open while mounted with `enabled`. A request shows up the moment it arrives and
 * follows its progress (status "pending" until it finishes); once persisted, the request list and that request's
 * detail are refetched (coalesced) so the stored row takes over.
 */
export function useLiveRequestFeed(enabled: boolean) {
  const qc = useQueryClient();
  useEffect(() => {
    if (!enabled) return;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const persisted = new Set<string>();
    const refetch = () => {
      if (timer) return;
      // Coalesce bursts (the usage writer persists in batches) into one list refetch.
      timer = setTimeout(() => {
        timer = undefined;
        void qc.invalidateQueries({ queryKey: ['requests'] });
        for (const id of persisted) void qc.invalidateQueries({ queryKey: ['request', id] });
        persisted.clear();
      }, 250);
    };
    if (consumers++ === 0)
      stop = connect(
        (id) => {
          persisted.add(id);
          refetch();
        },
        refetch,
      );
    return () => {
      if (timer) clearTimeout(timer);
      if (--consumers === 0) {
        stop?.();
        stop = undefined;
      }
    };
  }, [enabled, qc]);
}

/** In-flight rows, newest first; a new array only when membership or a filtered-on field changes. */
export const useLiveRequestList = () => useSyncExternalStore(store.subscribeList, store.getList);

/** Whether the live stream is up (callers poll instead while it is not). */
export const useLiveRequestsConnected = () => useSyncExternalStore(store.subscribeList, store.getConnected);

/** The live copy of one request, or undefined once it left the feed; re-renders only for that request. */
export function useLiveRequest(id: string): RequestSummary | undefined {
  return useSyncExternalStore(
    (listener) => store.subscribeRow(id, listener),
    () => store.get(id),
  );
}
