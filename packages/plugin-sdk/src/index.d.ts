// Typings of the `astra` module a v2 plugin imports (design/plugin-v2 02). The UI component props are generated from
// the host's schema (components.g.d.ts). Install with `npm i -D @astragate/plugin-sdk` and map `astra` to it:
//   tsconfig: "paths": { "astra": ["./node_modules/@astragate/plugin-sdk"] }
import type { ComponentName, UiBuilders, Format, Tone } from './components.g';
export type * from './components.g';

export type Json = string | number | boolean | null | Json[] | { [key: string]: Json };

/** A node of a view's tree: what render returns. */
export interface UiNode {
  type: ComponentName | string;
  key?: string;
  props: Record<string, unknown>;
  children?: UiNode[];
}

/** An event prop: an action name, or the action with its args. */
export type UiAction = string | { action: string; args?: Json };

/** A value per width class of the view's container (05 §1). */
export type Responsive<T> = { compact?: T; regular?: T; wide?: T };

/** A local condition (03 §6): evaluated by the shell over its local state bag, for visible / disabled. */
export type Condition = { when: Expr };
export type Expr = Json | ['local', string] | [('eq' | 'ne' | 'in' | 'gt' | 'lt'), Expr, Expr] | [('and' | 'or'), ...Expr[]] | [('not' | 'empty'), Expr];

export interface AstraError extends Error {
  code: 'permission_denied' | 'not_supported' | 'invalid_argument' | 'not_found' | 'rate_limited' | 'timeout' | 'quota_exceeded' | 'unavailable' | string;
  details?: Json;
}
export const AstraError: { new (code: string, message: string, details?: Json): AstraError };

export const version: string;
export const api: string;
export const env: { astraVersion: string; api: string; ui: string; platform: string; plugin: { id: string; version: string }; dev: boolean };
/** Whether an extension point, namespace or permission is there (has('gateway.hooks@2'), has('net:https://x')). */
export function has(what: string): boolean;

export interface ActivateContext {
  plugin: { id: string; version: string };
  reason: 'startup' | 'view' | 'command' | 'job' | 'event' | 'upgrade' | 'update' | 'restart' | string;
  /** On the first run after an upgrade from a v1 plugin (07 §3.3). */
  upgrade?: { from: { apiVersion: 1; package: string; spec: string; version?: string; options?: Json } };
  subscriptions: (Disposable | (() => void))[];
}
export interface PluginDefinition {
  activate?(ctx: ActivateContext): void | Promise<void>;
  deactivate?(): void | Promise<void>;
}
export function definePlugin<T extends PluginDefinition>(def: T): T;
export interface Disposable {
  dispose(): void;
}

export function t(key: string, params?: Record<string, string | number>): string;
export const i18n: { t: typeof t; readonly locale: string };

export const log: Record<'debug' | 'info' | 'warn' | 'error', (message: unknown, fields?: Json) => void>;

export const storage: {
  get<T = Json>(key: string): Promise<T | null>;
  set(key: string, value: Json): Promise<void>;
  delete(key: string): Promise<boolean>;
  list<T = Json>(o?: { prefix?: string; limit?: number; cursor?: string }): Promise<{ items: { key: string; value: T }[]; cursor?: string }>;
  /** Read-modify-write, one at a time per key. */
  update<T = Json>(key: string, fn: (value: T | null) => T | undefined | Promise<T | undefined>): Promise<T | null>;
};
export const secrets: { get(name: string): Promise<string | null>; set(name: string, value: string): Promise<void>; delete(name: string): Promise<boolean> };
export const config: { get<T = Record<string, Json>>(): T; onChange(fn: (config: Record<string, Json>) => void): Disposable };

export interface HttpResponse {
  status: number;
  ok: boolean;
  url: string;
  headers: Record<string, string>;
  /** Every `Set-Cookie` line of the reply (headers has no `set-cookie`: it can't be joined). Raw: the host keeps no cookie jar. */
  setCookies: string[];
  text(): Promise<string>;
  json<T = Json>(): Promise<T>;
  bytes(): Promise<Uint8Array>;
}
/** A reply fetched with `stream: true`: read it in chunks or lines (SSE, NDJSON); cancel() one you stop reading. */
export interface StreamedHttpResponse extends HttpResponse {
  streamed: true;
  stream(): AsyncIterable<Uint8Array>;
  lines(): AsyncIterable<string>;
  cancel(): Promise<void>;
}
export const http: {
  /**
   * Only origins in the plugin's net: permissions; https only; no private addresses. A `cookie` header may be sent; `redirect`
   * 'follow' (default) follows up to 5 hops, each checked against net: (a hop outside it fails with permission_denied) and
   * with `cookie` / `authorization` dropped once a hop leaves the first origin; 'manual' returns the 3xx (status, `location`
   * header, setCookies) untouched; 'error' fails with code `redirect_refused`.
   */
  fetch(url: string, init: { method?: string; headers?: Record<string, string>; body?: string | Json | Uint8Array; timeoutMs?: number; redirect?: 'follow' | 'manual' | 'error'; stream: true }): Promise<StreamedHttpResponse>;
  fetch(url: string, init?: { method?: string; headers?: Record<string, string>; body?: string | Json | Uint8Array; timeoutMs?: number; redirect?: 'follow' | 'manual' | 'error'; stream?: false }): Promise<HttpResponse>;
};

export const commands: { register(id: string, fn: (args: any, ctx: { client: Record<string, unknown> }) => unknown): Disposable; execute(id: string, args?: Json): Promise<Json> };
export const events: { on(name: string, fn: (payload: any) => void | Promise<void>): Disposable; emit(name: string, payload?: Json): void };
export const jobs: { handle(id: string, fn: (ctx: { scheduledAt: string; attempt: number }) => void | Promise<void>): Disposable; runNow(id: string): Promise<void> };
export const notifications: { send(o: string | { title: string; body?: string; tone?: Tone }): void };
export const permissions: { has(p: string): boolean; request(p: string | string[]): Promise<boolean> };
/** Bytes in: a string (its UTF-8), a Uint8Array / ArrayBuffer, or `{ base64 }` / `{ hex }`. */
export type CryptoData = string | Uint8Array | ArrayBuffer | { base64: string } | { hex: string };
/** What a result is: a hex / base64 / base64url string, UTF-8 text (decrypt only), or a Uint8Array. */
export type CryptoEncoding = 'hex' | 'base64' | 'base64url' | 'utf8' | 'bytes';
/** The type a result has for its encoding: a Uint8Array for 'bytes', else a string. */
export type CryptoOut<E> = E extends 'bytes' ? Uint8Array : string;
export type DigestAlgorithm = 'md5' | 'sha1' | 'sha256' | 'sha384' | 'sha512';
export type SignAlgorithm = 'rsa-sha1' | 'rsa-sha256' | 'rsa-sha384' | 'rsa-sha512' | 'rsa-pss-sha256' | 'ecdsa-p256-sha256' | 'ecdsa-p384-sha384';
/** Pure computation: no permission, and gateway hooks may call it. MD5 and SHA-1 are for speaking to vendors, not for new designs. A bad key or size throws an AstraError `invalid_argument`. */
export const crypto: {
  /** Hex by default; `encoding: 'bytes'` for a Uint8Array. */
  digest<E extends CryptoEncoding = 'hex'>(algorithm: DigestAlgorithm, data: CryptoData, o?: { encoding?: E }): CryptoOut<E>;
  sha256<E extends CryptoEncoding = 'hex'>(data: CryptoData, o?: { encoding?: E }): CryptoOut<E>;
  hmac<E extends CryptoEncoding = 'hex'>(key: CryptoData, data: CryptoData, o?: DigestAlgorithm | { algorithm?: DigestAlgorithm; encoding?: E }): CryptoOut<E>;
  aes: {
    /** cbc (PKCS#7 unless padding: 'none'; 16-byte iv), ecb, or gcm (12-byte iv; the 16-byte tag is appended). Base64 by default. */
    encrypt<E extends CryptoEncoding = 'base64'>(o: { mode?: 'cbc' | 'ecb' | 'gcm'; key: CryptoData; iv?: CryptoData; data: CryptoData; aad?: CryptoData; padding?: 'pkcs7' | 'none'; encoding?: E }): CryptoOut<E>;
    /** The same options (gcm data ends with its tag, or pass `tag`). A Uint8Array by default; `encoding: 'utf8'` for text. */
    decrypt<E extends CryptoEncoding = 'bytes'>(o: { mode?: 'cbc' | 'ecb' | 'gcm'; key: CryptoData; iv?: CryptoData; data: CryptoData; aad?: CryptoData; tag?: CryptoData; padding?: 'pkcs7' | 'none'; encoding?: E }): CryptoOut<E>;
  };
  rsa: {
    /** `key`: a public key as PEM (SPKI or PKCS#1) or base64 DER. oaep's hash is sha1 unless given. Base64 by default. */
    encrypt<E extends CryptoEncoding = 'base64'>(o: { key: string; data: CryptoData; padding?: 'pkcs1' | 'oaep'; hash?: DigestAlgorithm; encoding?: E }): CryptoOut<E>;
  };
  /** `key`: a private key as PEM (PKCS#8, PKCS#1, SEC1) or base64 DER. ECDSA gives DER unless `format: 'raw'` (r||s). Base64 by default. */
  sign<E extends CryptoEncoding = 'base64'>(o: { algorithm: SignAlgorithm; key: string; data: CryptoData; format?: 'der' | 'raw'; encoding?: E }): CryptoOut<E>;
  /** `key`: the public key. A string `signature` is UTF-8: pass `{ base64 }` for base64. */
  verify(o: { algorithm: SignAlgorithm; key: string; data: CryptoData; signature: CryptoData; format?: 'der' | 'raw' }): boolean;
  /** iterations ≤ 1,000,000 (default 100000), length ≤ 1024 (default 32), hash sha256. Hex by default. */
  pbkdf2<E extends CryptoEncoding = 'hex'>(o: { password: CryptoData; salt: CryptoData; iterations?: number; length?: number; hash?: DigestAlgorithm; encoding?: E }): CryptoOut<E>;
  hkdf<E extends CryptoEncoding = 'hex'>(o: { key: CryptoData; salt?: CryptoData; info?: CryptoData; length?: number; hash?: DigestAlgorithm; encoding?: E }): CryptoOut<E>;
  randomUUID(): string;
  randomBytes(n: number): Uint8Array;
};
/** Bytes <-> text without Buffer: encode takes a Uint8Array (a string is its UTF-8), decode returns a Uint8Array and throws on bad input. */
export const encoding: {
  base64: { encode(data: string | Uint8Array | ArrayBuffer): string; decode(text: string): Uint8Array };
  base64url: { encode(data: string | Uint8Array | ArrayBuffer): string; decode(text: string): Uint8Array };
  hex: { encode(data: string | Uint8Array | ArrayBuffer): string; decode(text: string): Uint8Array };
  utf8: { encode(text: string): Uint8Array; decode(data: Uint8Array | ArrayBuffer): string };
};
export const timers: {
  sleep(ms: number): Promise<void>;
  setTimeout(fn: (...a: any[]) => void, ms: number, ...args: any[]): number;
  clearTimeout(id: number): void;
  setInterval(fn: (...a: any[]) => void, ms: number, ...args: any[]): number;
  clearInterval(id: number): void;
};
export const data: {
  requests: { query(o?: Record<string, Json>): Promise<{ total: number; items: Record<string, Json>[] }>; stats(o?: Record<string, Json>): Promise<Record<string, number>> };
  providers: { list(): Promise<Record<string, Json>[]> };
  models: { list(): Promise<Record<string, Json>[]> };
  tokens: { list(): Promise<Record<string, Json>[]> };
};

// ---------- views (03 §4)

export interface ViewContext {
  params: Record<string, Json>;
  query: Record<string, string>;
  client: { shell: 'web' | 'desktop' | string; ui: string; locale: string; theme: 'light' | 'dark'; width: 'compact' | 'regular' | 'wide'; surface: string; timeZone?: string; utcOffset?: number; components?: string[] };
  local: Record<string, Json>;
  session: string;
  t: typeof t;
  toast(o: string | { message: string; tone?: Tone; action?: UiAction }): void;
  navigate(to: string | { host?: string; page?: string; path?: string; query?: Record<string, string> }): void;
  back(): void;
  openExternal(url: string): void;
  copy(text: string): void;
  download(o: { name: string; text?: string; bytes?: Uint8Array; type?: string }): void;
  focus(key: string): void;
  scrollTo(key: string): void;
  close(result?: Json): void;
  /** Field errors, by input name; the form stays dirty. */
  fail(errors: Record<string, string>): void;
  setQuery(query: Record<string, string | null>): void;
  /** Pushes the state as it is now (progress inside a long action). */
  flush(): void;
  refresh(): void;
  confirm(o: string | { title: string; message?: string; confirmLabel?: string; tone?: Tone }): Promise<boolean>;
  prompt(o: string | { title: string; label?: string; value?: string; secret?: boolean }): Promise<string | null>;
  pickFile(o?: { accept?: string; multiple?: boolean }): Promise<{ ref: string; name: string; size: number; type: string } | null>;
  openSheet(view: string, params?: Json, opts?: { title?: string; size?: 'sm' | 'md' | 'lg' }): Promise<Json>;
  openDialog(view: string, params?: Json, opts?: { title?: string }): Promise<Json>;
  collection(name: string): Collection;
}
export interface Collection {
  replace(items: Json[], meta?: { total?: number; page?: number; key?: string }): void;
  append(items: Json | Json[]): void;
  prepend(items: Json | Json[]): void;
  update(key: string | number, patch: Record<string, Json>): void;
  remove(key: string | number): void;
  clear(): void;
  setChildren(key: string, nodes: Json[]): void;
}
export interface ViewDefinition<S> {
  init?(ctx: ViewContext): S | Promise<S>;
  state?: S;
  render(state: S, ctx: ViewContext): UiNode | UiNode[];
  actions?: Record<string, (state: S, args: any, ctx: ViewContext) => void | S | Promise<void | S>>;
  subscribe?(ctx: ViewContext): (() => void) | Disposable | void;
  refresh?(state: S, ctx: ViewContext): void | Promise<void>;
}
export const ui: UiBuilders & {
  node(type: string, props?: Record<string, unknown>, children?: unknown): UiNode;
  defineView<S>(def: ViewDefinition<S>): ViewDefinition<S>;
  /** A data channel a Table / List / Tree / Chart / LogView reads (source). */
  collection(name: string): { collection: string };
  supports(name: string, client?: ViewContext['client']): boolean;
  files: { read(ref: string): Promise<{ name: string; type: string; size: number; bytes: Uint8Array; text(): string }> };
  when(expr: Expr): Condition;
  local(name: string): ['local', string];
  dates: { toUtcRange(range: { from: string; to: string }, utcOffsetMinutes: number): { fromUtc: string; toUtc: string } };
};
export type { Format, Tone };

// ---------- gateway hooks (02 §3)

export interface HookContext {
  protocol: 'anthropic' | 'chat' | 'responses' | 'gemini';
  model: string;
  stream: boolean;
  path: string;
  client: string | null;
  token: { id: string; name: string } | null;
  requestId?: string;
  options: Record<string, Json>;
  shared: Record<string, Json>;
  state: Record<string, unknown>;
  status?: number;
  upstream?: { protocol: string; provider: { id: string; name: string }; model: string; stream: boolean };
  reject(status: number, message?: string): void;
  setHeader(name: string, value: string): void;
  removeHeader(name: string): void;
  emit(name: string, payload?: Json): void;
  count(name: string, n?: number): void;
  holdBack(n: number): void;
  sha256(data: string | number[]): string;
}
export interface HookSet {
  onRequest?(req: any, ctx: HookContext): any;
  onUpstream?(req: any, ctx: HookContext): any;
  onEvent?(event: any, ctx: HookContext): any;
  onText?(text: string, ctx: HookContext): string | undefined;
  onResponse?(res: any, ctx: HookContext): any;
  onComplete?(info: { status: number; usage?: Record<string, number>; durationMs: number; error?: string }, ctx: HookContext): void;
}
export const gateway: { defineHooks<T extends Record<string, HookSet>>(sets: T): T };

// ---------- providers and clients (2.1, design/plugin-v2 08)

/** What the host is about to send upstream: the credential and transport headers already taken out. */
export interface ProviderRequest {
  /** the plugin-local provider id (providers.register's first argument) */
  provider: string;
  protocol: 'chat' | 'responses' | 'anthropic' | 'gemini';
  model: string | null;
  path: string;
  method: string;
  headers: Record<string, string>;
  body: Json | null;
  stream: boolean;
}
/** A frame of the upstream reply, as the host cuts it for the provider's manifest \`reply.framing\` (08 §2.2). */
export type ReplyFrame =
  | { event?: string; data: string; id?: string; json?: Json }              // sse (data lines joined by \n; json when data parses)
  | { json: Json } | { text: string }                                       // ndjson (one per line) and json (the whole body)
  | { headers: Record<string, Json>; json?: Json; text?: string; bytes?: string } // aws-eventstream (bytes: base64, only when not UTF-8 text)
  | { flags: number; bytes: string }                                         // connect: a data message (bytes: base64, usually protobuf)
  | { flags: number; end: true; json?: Json; text?: string };              // connect: the end-of-stream message (Connect's JSON trailer)
/**
 * What a provider's transformResponse returns for a frame: output frames in the \`reply.output\` protocol's shape.
 * \`{ $end: true }\` says the reply is complete: the host stops reading, closes the upstream (one that would stay open — a
 * bidirectional stream waiting on the client) and calls end.
 */
export type ReplyOutput = Json | { event: string; data: Json } | '[DONE]' | { $error: { status?: number; message: string } } | { $end: true };
export interface ReplyContext {
  request: { protocol: string; output: string; model: string | null; stream: boolean };
  account: ProviderAccount | null;
  /** one object per reply, shared by start, every frame, end and dispose; it starts as a copy of what prepare returned as \`state\` */
  state: Record<string, any>;
}
export interface TransformResponse {
  /** The upstream status and headers (a refusal also carries \`body\`, an excerpt); return { status?, headers? } to change them, or { error } to refuse the reply (it reaches the gateway as that status). */
  start?(res: { status: number; headers: Record<string, string>; stream: boolean; body?: string }, ctx: ReplyContext): { status?: number; headers?: Record<string, string>; error?: { status?: number; message: string } } | void | Promise<{ status?: number; headers?: Record<string, string>; error?: { status?: number; message: string } } | void>;
  /** Once per upstream frame; returns none, one or several output frames. Streaming requests: objects the host writes as SSE data events ({ event, data } pairs for anthropic); non-streaming: exactly one complete body object over the whole reply. */
  frame(frame: ReplyFrame, ctx: ReplyContext): ReplyOutput | ReplyOutput[] | void | Promise<ReplyOutput | ReplyOutput[] | void>;
  /** After the last frame: flush what was buffered (the host adds \`data: [DONE]\` to a chat stream itself). */
  end?(ctx: ReplyContext): ReplyOutput | ReplyOutput[] | void | Promise<ReplyOutput | ReplyOutput[] | void>;
  /** The reply is over — ended, failed, or the client went away (always called, also without end): stop timers and side calls. */
  dispose?(ctx: ReplyContext): void | Promise<void>;
}
/** What prepare returns. \`body\`: JSON, or bytes (content-type from \`headers\`, else application/octet-stream); \`state\`: JSON the reply's ctx.state starts with. */
export interface PreparedRequest { url: string; headers?: Record<string, string>; body?: Json | Uint8Array; method?: string; state?: Record<string, Json> }
export interface ProviderAccount {
  key: string;
  /** The credential the host keeps for this account (providers.accounts.add({ secret })). */
  secret(): Promise<string | null>;
}
export interface ProviderImpl {
  /** Where and how the host sends the request: an https url the plugin's net: permissions cover, headers, maybe a new body. */
  prepare(req: ProviderRequest, account: ProviderAccount | null): Promise<PreparedRequest> | PreparedRequest;
  models?(account: ProviderAccount | null): Promise<{ id: string; name?: string; protocol?: string; context?: number; output?: number; vision?: boolean; reasoning?: boolean }[]>;
  /** After a 401 / 403 upstream, with the refused reply's status and the first 4 KB of its body: "renewed" keeps the account, "expired" marks it. */
  refresh?(account: ProviderAccount, failed: { status: number; body: string }): Promise<'renewed' | 'expired' | void>;
  /** Not called by the host yet (accepted so a plugin can already ship it). */
  usage?(account: ProviderAccount): Promise<Json>;
  /** Translates an upstream reply that is not in one of the provider's wire formats; needs the manifest's \`reply\` block (and only works with it). */
  transformResponse?: TransformResponse;
}
export interface ProviderAccountInfo { key: string; displayName?: string; email?: string | null; plan?: string | null; status?: 'active' | 'expired' | 'revoked'; enabled?: boolean }
export const providers: {
  register(id: string, impl: ProviderImpl): Disposable;
  accounts: {
    list(provider: string): Promise<ProviderAccountInfo[]>;
    add(provider: string, account: ProviderAccountInfo & { secret?: string }): Promise<void>;
    update(provider: string, key: string, patch: Partial<ProviderAccountInfo> & { secret?: string }): Promise<void>;
    remove(provider: string, key: string): Promise<boolean>;
  };
};
export interface ClientConnectInput {
  gateway: { url: string; v1: string; key: string };
  model?: string;
  models?: { id: string; name?: string; context?: number; output?: number; images?: boolean; efforts?: string[] }[];
  agent?: { id: string; config: string };
}
export const clients: { register(id: string, impl: { connect?(input: ClientConnectInput): Record<string, Json> | void | Promise<Record<string, Json> | void> }): Disposable };

export function jsx(type: string | ((props: any) => unknown), props: Record<string, unknown>, key?: string): UiNode;
export const jsxs: typeof jsx;
export function Fragment(props: { children?: unknown }): UiNode[];
