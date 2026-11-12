import type { ClientConnectInput, HookSet, PluginDefinition, ProviderRequest, UiNode, ViewDefinition } from './index.js';

export interface MountOptions {
  name?: string;
  params?: Record<string, unknown>;
  query?: Record<string, string>;
  client?: Record<string, unknown>;
  storage?: Record<string, unknown>;
  secrets?: Record<string, string>;
  config?: Record<string, unknown>;
  messages?: Record<string, Record<string, string>>;
  permissions?: string[];
  fetch?: (req: { url: string; method: string; headers: Record<string, string>; body: string | null; stream?: boolean; redirect?: 'follow' | 'manual' | 'error' }) => Promise<{ status?: number; headers?: Record<string, string>; /** the reply's Set-Cookie lines */ setCookies?: string[]; body?: unknown; /** with stream: true, how the body arrives */ chunks?: (string | Uint8Array)[] }>;
  confirm?: boolean | ((effect: unknown) => boolean | Promise<boolean>);
  prompt?: string | null | ((effect: unknown) => string | null | Promise<string | null>);
  pickFile?: unknown;
  openSheet?: unknown;
  openDialog?: unknown;
  /** false: tree errors go to warnings instead of failing the render */
  strict?: boolean;
  /** the slot the view fills, for its root check (tray.panel) */
  slot?: string;
}

export interface Mounted<S> {
  state: S;
  tree: UiNode;
  actions: string[];
  effects: { type: string; [k: string]: unknown }[];
  /** what the dev-time checks found (and, with strict: false, the errors too) */
  warnings: string[];
  storage: { get(key: string): Promise<unknown> };
  host: { logs: unknown[]; notifications: unknown[]; emitted: unknown[]; data: unknown[]; asked: unknown[]; flushed: unknown[] };
  render(): Promise<UiNode>;
  press(action: string, args?: Record<string, unknown>): Promise<{ type: string; [k: string]: unknown }[]>;
  change(nameOrKey: string, value: unknown): Promise<void>;
  find(typeOrKey: string): UiNode | null;
  findAll(type: string): UiNode[];
  close(): Promise<void>;
}

export function mount<S>(view: ViewDefinition<S>, opts?: MountOptions): Promise<Mounted<S>>;
export function runCommand(plugin: PluginDefinition, id: string, args?: unknown, opts?: MountOptions): Promise<unknown>;
export function runHook(
  hooks: Record<string, HookSet>,
  setId: string,
  hook: 'request' | 'upstream' | 'event' | 'text' | 'response' | 'complete',
  input: unknown,
  opts?: Record<string, unknown>,
): Promise<{ output: unknown; rejected: { status: number; message: string } | null; headers: [string, string | null][]; emitted: { name: string; payload: unknown }[]; counts: Record<string, number>; held: number }>;

export { validateTree } from './validate-tree.mjs';
export function runProvider(plugin: PluginDefinition, providerId: string, what: 'prepare' | 'models' | 'refresh' | 'usage',
  request?: ProviderRequest | null, account?: { key: string; secret?: string } | null, opts?: MountOptions): Promise<unknown>;
export interface RunResponseInput {
  /** how the host frames the upstream reply (the manifest's reply.framing); default sse */
  framing?: 'sse' | 'ndjson' | 'json' | 'aws-eventstream' | 'connect';
  /** the frames, as the host hands them to frame(): sse { event?, data, id?, json? }, ndjson / json { json | text }, aws-eventstream { headers, json | text, bytes? }, connect { flags, bytes } / { flags, end: true, json | text } */
  frames?: Record<string, unknown>[];
  /** framing "json": the whole body (an object, or a string for { text }) */
  body?: unknown;
  /** ctx.request: protocol, model, stream (default true) */
  request?: { protocol?: string; model?: string | null; stream?: boolean };
  /** the upstream reply start() sees (status, headers, body for a refusal) */
  response?: { status?: number; headers?: Record<string, string>; body?: string };
  account?: { key: string; secret?: string } | null;
  /** what prepare returned as \`state\`: ctx.state starts as a copy of it */
  state?: Record<string, unknown>;
}
export interface RunResponseResult {
  status: number;
  headers: Record<string, string>;
  /** start() refused the reply: the gateway sees this status and message */
  error: { status?: number; message?: string } | null;
  /** a frame / end result was { $error: { status, message } } */
  failure: { status?: number; message?: string } | null;
  /** the output frames, in the order the host writes them */
  frames: unknown[];
  /** the single body of a non-streaming reply (request.stream: false) */
  body?: unknown;
  /** a frame returned { $end: true }: the frames after it were not read */
  ended: boolean;
  /** how many input frames frame() was called with */
  consumed: number;
}
export function runResponse(plugin: PluginDefinition, providerId: string, input: RunResponseInput, opts?: MountOptions): Promise<RunResponseResult>;
export function runClient(plugin: PluginDefinition, clientId: string, input: ClientConnectInput, opts?: MountOptions): Promise<Record<string, unknown> | null>;
