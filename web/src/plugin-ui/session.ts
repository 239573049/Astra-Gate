import { useCallback, useEffect, useRef, useState } from 'react';

import { api, apiBase } from '../api/client';
import {
  applyPatch,
  UI_VERSION,
  type UiCollectionState,
  type UiEffect,
  type UiMessage,
  type UiNode,
  type WidthClass,
} from './protocol';
import { COMPONENTS } from './components';

export interface ViewTarget {
  plugin: string;
  view: string;
  params?: Record<string, unknown>;
  query?: Record<string, unknown>;
  slot?: string;
  parent?: string;
}

export interface ClientContext {
  locale: string;
  theme: 'light' | 'dark';
  width: WidthClass;
  surface: 'page' | 'sheet' | 'slot' | 'tray' | 'dialog';
}

export interface ViewSession {
  status: 'opening' | 'open' | 'error' | 'closed';
  error?: string;
  tree?: UiNode;
  collections: Record<string, UiCollectionState>;
  /** Field errors set by ctx.fail, by input name. */
  fieldErrors: Record<string, string>;
  /** The key of the control whose action is in flight longer than 150 ms (automatic busy state, 03 §6.3). */
  busyKey: string | null;
  asks: { id: string; effect: UiEffect }[];
  local: React.MutableRefObject<Record<string, unknown>>;
  /** Per input name, the seq of the last local edit (03 §6.1): a patch acknowledging less doesn't overwrite it. */
  editedAt: React.MutableRefObject<Record<string, number>>;
  ack: React.MutableRefObject<number>;
  send: (action: string, args: unknown, sourceKey?: string) => void;
  setLocal: (name: string, value: unknown) => void;
  answer: (id: string, value: unknown) => void;
  uploadFile: (file: File) => Promise<unknown>;
  sessionId: string | null;
}

/**
 * One open plugin view (design/plugin-v2 03 §3.2): opens a session, follows its event stream (reconnecting with the
 * revision it has), applies patches, keeps the local state bag, sends events with a sequence number, and hands effects
 * to <paramref name="onEffect"/>.
 */
export function useViewSession(
  target: ViewTarget | null,
  client: ClientContext,
  onEffect: (e: UiEffect) => void,
): ViewSession {
  const [status, setStatus] = useState<ViewSession['status']>('opening');
  const [error, setError] = useState<string>();
  const [tree, setTree] = useState<UiNode>();
  const [collections, setCollections] = useState<Record<string, UiCollectionState>>({});
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({});
  const [busyKey, setBusyKey] = useState<string | null>(null);
  const [asks, setAsks] = useState<{ id: string; effect: UiEffect }[]>([]);
  const [sessionId, setSessionId] = useState<string | null>(null);
  const local = useRef<Record<string, unknown>>({});
  const editedAt = useRef<Record<string, number>>({});
  const ack = useRef(0);
  const seq = useRef(0);
  const rev = useRef(0);
  const treeRef = useRef<UiNode | undefined>(undefined);
  const effectRef = useRef(onEffect);
  effectRef.current = onEffect;
  const clientRef = useRef(client);
  const key = target ? JSON.stringify(target) : '';

  const clientJson = useCallback(
    (c: ClientContext) => ({
      shell: 'web',
      ui: UI_VERSION,
      components: COMPONENTS,
      locale: c.locale,
      theme: c.theme,
      width: c.width,
      surface: c.surface,
      platform: 'browser',
      timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
      utcOffset: -new Date().getTimezoneOffset(),
      weekStart: c.locale.startsWith('zh') ? 1 : 0,
    }),
    [],
  );

  useEffect(() => {
    if (!target) return;
    let cancelled = false;
    let source: EventSource | null = null;
    let id: string | null = null;
    let retry: ReturnType<typeof setTimeout> | undefined;
    setStatus('opening');
    setTree(undefined);
    setCollections({});
    local.current = {};
    editedAt.current = {};

    const handle = (m: UiMessage) => {
      switch (m.type) {
        case 'patch': {
          if (m.base !== rev.current && m.ops.length > 0) {
            // missed something: reconnect from what we have, the server sends a reset
            reconnect();
            return;
          }
          rev.current = m.rev;
          if (m.ack !== undefined) {
            ack.current = Math.max(ack.current, m.ack);
            setBusyKey(null);
          }
          if (m.ops.length && treeRef.current) {
            treeRef.current = applyPatch(treeRef.current, m.ops);
            setTree(treeRef.current);
          }
          return;
        }
        case 'reset':
          rev.current = m.rev;
          treeRef.current = m.tree;
          setTree(m.tree);
          setCollections(m.collections ?? {});
          return;
        case 'effect':
          if (m.effect.type === 'fail')
            setFieldErrors((m.effect.errors as Record<string, string>) ?? {});
          else effectRef.current(m.effect);
          return;
        case 'ask':
          setAsks((list) =>
            list.some((a) => a.id === m.id) ? list : [...list, { id: m.id, effect: m.effect }],
          );
          return;
        case 'data':
          setCollections((all) => applyData(all, m));
          return;
        case 'error':
          effectRef.current({ type: 'toast', tone: 'danger', message: m.message, error: true });
          return;
        case 'closed':
          setStatus('closed');
          setError(m.reason);
          source?.close();
          return;
      }
    };

    const connect = () => {
      if (!id || cancelled) return;
      source = new EventSource(
        `${apiBase()}/api/plugins/v2/ui/sessions/${id}/stream?rev=${rev.current}`,
        { withCredentials: true },
      );
      source.onmessage = (e) => {
        try {
          handle(JSON.parse(e.data) as UiMessage);
        } catch {
          // a malformed message is skipped
        }
      };
      source.onerror = () => {
        source?.close();
        if (!cancelled) retry = setTimeout(connect, 1500);
      };
    };
    const reconnect = () => {
      source?.close();
      connect();
    };

    api<{ session: string; rev: number; tree: UiNode; warnings: string[] }>(
      'POST',
      '/api/plugins/v2/ui/sessions',
      {
        plugin: target.plugin,
        view: target.view,
        params: target.params ?? {},
        query: target.query ?? {},
        slot: target.slot,
        parent: target.parent,
        client: clientJson(clientRef.current),
      },
    ).then(
      (open) => {
        if (cancelled) {
          void api('DELETE', `/api/plugins/v2/ui/sessions/${open.session}`).catch(() => {});
          return;
        }
        id = open.session;
        rev.current = open.rev;
        treeRef.current = open.tree;
        setSessionId(open.session);
        setTree(open.tree);
        setStatus('open');
        connect();
      },
      (err: Error) => {
        if (!cancelled) {
          setStatus('error');
          setError(err.message);
        }
      },
    );

    return () => {
      cancelled = true;
      clearTimeout(retry);
      source?.close();
      if (id) void api('DELETE', `/api/plugins/v2/ui/sessions/${id}`).catch(() => {});
      setSessionId(null);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [key]);

  // language, theme or width changed: the view renders again
  useEffect(() => {
    const prev = clientRef.current;
    clientRef.current = client;
    if (!sessionId) return;
    if (prev.locale === client.locale && prev.theme === client.theme && prev.width === client.width)
      return;
    void api('POST', `/api/plugins/v2/ui/sessions/${sessionId}/client`, {
      client: clientJson(client),
    }).catch(() => {});
  }, [client, sessionId, clientJson]);

  const send = useCallback(
    (action: string, args: unknown, sourceKey?: string) => {
      if (!sessionId) return;
      const n = ++seq.current;
      const timer = setTimeout(() => setBusyKey(sourceKey ?? action), 150);
      setFieldErrors({});
      api('POST', `/api/plugins/v2/ui/sessions/${sessionId}/events`, {
        action,
        args: args ?? null,
        local: local.current,
        seq: n,
      })
        .catch((err: Error) =>
          effectRef.current({ type: 'toast', tone: 'danger', message: err.message, error: true }),
        )
        .finally(() => {
          clearTimeout(timer);
          // the patch that acknowledges it clears the busy state; a failed post never gets one
          setTimeout(() => setBusyKey((k) => (k === (sourceKey ?? action) ? null : k)), 3000);
        });
    },
    [sessionId],
  );

  const setLocal = useCallback((name: string, value: unknown) => {
    local.current = { ...local.current, [name]: value };
    editedAt.current[name] = seq.current + 1;
  }, []);

  const answer = useCallback(
    (id: string, value: unknown) => {
      setAsks((list) => list.filter((a) => a.id !== id));
      if (sessionId)
        void api('POST', `/api/plugins/v2/ui/sessions/${sessionId}/answers`, {
          id,
          value: value ?? null,
        }).catch(() => {});
    },
    [sessionId],
  );

  const uploadFile = useCallback(
    async (file: File) => {
      if (!sessionId) return null;
      const buf = new Uint8Array(await file.arrayBuffer());
      let bin = '';
      for (let i = 0; i < buf.length; i += 0x8000)
        bin += String.fromCharCode(...buf.subarray(i, i + 0x8000));
      return api('POST', `/api/plugins/v2/ui/sessions/${sessionId}/files`, {
        name: file.name,
        type: file.type || 'application/octet-stream',
        base64: btoa(bin),
      });
    },
    [sessionId],
  );

  return {
    status,
    error,
    tree,
    collections,
    fieldErrors,
    busyKey,
    asks,
    local,
    editedAt,
    ack,
    send,
    setLocal,
    answer,
    uploadFile,
    sessionId,
  };
}

function applyData(
  all: Record<string, UiCollectionState>,
  m: Extract<UiMessage, { type: 'data' }>,
): Record<string, UiCollectionState> {
  const c: UiCollectionState = all[m.collection] ?? { items: [], meta: {}, children: {} };
  const keyField = (c.meta.key as string) || 'id';
  let items = c.items;
  let meta = c.meta;
  let children = c.children;
  const incoming = (m.items ?? []) as Record<string, unknown>[];
  switch (m.op) {
    case 'replace':
      items = incoming;
      meta = m.meta ?? {};
      break;
    case 'append':
      items = [...items, ...incoming];
      break;
    case 'prepend':
      items = [...incoming, ...items];
      break;
    case 'update':
      items = items.map((i) =>
        String(i[keyField]) === String(m.key) ? { ...i, ...(m.patch ?? {}) } : i,
      );
      break;
    case 'remove':
      items = items.filter((i) => String(i[keyField]) !== String(m.key));
      break;
    case 'clear':
      items = [];
      break;
    case 'children':
      children = { ...children, [String(m.key)]: incoming };
      break;
  }
  if (items.length > 10_000) items = items.slice(items.length - 10_000);
  return { ...all, [m.collection]: { items, meta, children } };
}
