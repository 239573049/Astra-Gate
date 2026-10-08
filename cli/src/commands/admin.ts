import { AstraError } from '../errors.js';
import { printTable, success } from '../ui.js';
import { apiRequest, ApiError } from '../lib/http.js';
import { ensureServerRunning } from '../lib/server-lifecycle.js';
import { CLIENT_KINDS, type ClientKind } from '../lib/platform.js';

interface ClientRow {
  kind: string;
  name?: string;
  enabled?: boolean;
  status?: string;
  providerId?: string;
  selectedModel?: string;
  availability?: string;
  tokenId?: string;
}

interface TokenStatsRow {
  costUsd?: number;
  requests?: number;
  totalTokens?: number;
}

interface TokenRow {
  id: string;
  name?: string;
  isDefault?: boolean;
  enabled?: boolean;
  clients?: string[];
  today?: TokenStatsRow;
  total?: TokenStatsRow;
}

interface ProviderRow {
  id: string;
  name?: string;
  templateId?: string;
  enabled?: boolean;
  modelCount?: number;
}

interface TemplateRow {
  id: string;
  name?: string;
  variants?: { id: string; name?: string }[];
}

interface ApiCallOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE';
  body?: unknown;
}

async function api<T>(path: string, options: ApiCallOptions = {}): Promise<T> {
  const { base, runtime, started } = await ensureServerRunning();
  if (started) console.log('Server was not running — started it in the background.');
  return apiRequest<T>({
    base,
    path,
    method: options.method ?? 'GET',
    body: options.body,
    runtimeToken: runtime?.runtimeToken,
  });
}

function fmtBool(v: boolean | undefined): string {
  if (v === undefined) return '';
  return v ? 'yes' : 'no';
}

function findProvider(providers: ProviderRow[], idOrName: string): ProviderRow | undefined {
  const needle = idOrName.toLowerCase();
  const byId = providers.find((p) => p.id.toLowerCase() === needle);
  if (byId) return byId;
  const byName = providers.filter((p) => p.name?.toLowerCase() === needle);
  if (byName.length === 1) return byName[0];
  if (byName.length > 1) {
    throw new AstraError(
      `Provider name "${idOrName}" is ambiguous.`,
      `Matching ids: ${byName.map((p) => p.id).join(', ')} — use an id instead.`,
    );
  }
  return undefined;
}

function findToken(tokens: TokenRow[], idOrName: string): TokenRow | undefined {
  const needle = idOrName.toLowerCase();
  const byId = tokens.find((t) => t.id.toLowerCase() === needle);
  if (byId) return byId;
  const byName = tokens.filter((t) => t.name?.toLowerCase() === needle);
  if (byName.length === 1) return byName[0];
  if (byName.length > 1) {
    throw new AstraError(
      `Token name "${idOrName}" is ambiguous.`,
      `Matching ids: ${byName.map((t) => t.id).join(', ')} — use an id instead.`,
    );
  }
  return undefined;
}

function fmtUsd(v: number | undefined): string {
  return v === undefined ? '' : `$${v.toFixed(v !== 0 && Math.abs(v) < 0.01 ? 4 : 2)}`;
}

// ---------- client ----------

export async function runClientList(): Promise<void> {
  const rows = await api<ClientRow[]>('/api/clients');
  printTable(
    rows.map((r) => [
      r.kind,
      r.name ?? '',
      fmtBool(r.enabled),
      r.status ?? '',
      r.providerId ?? '',
      r.selectedModel ?? '',
      r.availability ?? '',
    ]),
    ['KIND', 'NAME', 'ENABLED', 'STATUS', 'PROVIDER', 'MODEL', 'AVAILABILITY'],
  );
}

export async function runClientStatus(kind: ClientKind): Promise<void> {
  const rows = await api<ClientRow[]>('/api/clients');
  const row = rows.find((r) => r.kind === kind);
  if (!row) throw new AstraError(`Unknown client kind: ${kind}.`);
  console.log(`Client: ${row.name ?? row.kind} (${row.kind})`);
  console.log(`  Enabled      ${fmtBool(row.enabled)}`);
  if (row.status) console.log(`  Status       ${row.status}`);
  if (row.availability) console.log(`  Availability ${row.availability}`);
  if (row.providerId) console.log(`  Provider     ${row.providerId}`);
  if (row.selectedModel) console.log(`  Model        ${row.selectedModel}`);
  if (row.tokenId) console.log(`  Token        ${row.tokenId}`);
}

/**
 * Enables a client. `token` (id or name) picks the token written into its config; omitted, the server keeps the
 * client's current token, or uses the default token for a client that never had one.
 */
export async function runClientEnable(
  kind: ClientKind,
  opts: { provider: string; model?: string; token?: string },
): Promise<void> {
  const providers = await api<ProviderRow[]>('/api/providers');
  const provider = findProvider(providers, opts.provider);
  if (!provider) {
    throw new AstraError(
      `No provider matches "${opts.provider}".`,
      'Run `astra provider list` to see available providers.',
    );
  }
  let token: TokenRow | undefined;
  if (opts.token) {
    token = findToken(await api<TokenRow[]>('/api/tokens'), opts.token);
    if (!token) {
      throw new AstraError(
        `No token matches "${opts.token}".`,
        'Run `astra token list` to see available tokens.',
      );
    }
  }
  await api(`/api/clients/${kind}/enable`, {
    method: 'POST',
    body: {
      providerId: provider.id,
      ...(opts.model ? { model: opts.model } : {}),
      ...(token ? { tokenId: token.id } : {}),
    },
  });
  success(
    `Client "${kind}" enabled with provider "${provider.name ?? provider.id}"` +
      `${opts.model ? ` (model: ${opts.model})` : ''}` +
      `${token ? ` (token: ${token.name ?? token.id})` : ''}.`,
  );
  console.log('Restart the client (Codex, Claude Desktop, …) if it does not pick up the change.');
}

export async function runClientDisable(kind: ClientKind): Promise<void> {
  await api(`/api/clients/${kind}/disable`, { method: 'POST', body: {} });
  success(`Client "${kind}" disabled. Its original configuration was restored.`);
}

// ---------- token ----------

/** Lists tokens with today's and lifetime usage; the plaintext token is never printed. */
export async function runTokenList(): Promise<void> {
  const rows = await api<TokenRow[]>('/api/tokens');
  printTable(
    rows.map((r) => [
      r.id,
      r.name ?? '',
      fmtBool(r.isDefault),
      fmtBool(r.enabled),
      (r.clients ?? []).join(','),
      fmtUsd(r.today?.costUsd),
      r.today?.totalTokens === undefined ? '' : String(r.today.totalTokens),
      fmtUsd(r.total?.costUsd),
    ]),
    ['ID', 'NAME', 'DEFAULT', 'ENABLED', 'CLIENTS', 'TODAY COST', 'TODAY TOKENS', 'TOTAL COST'],
  );
}

// ---------- provider ----------

export async function runProviderList(): Promise<void> {
  const rows = await api<ProviderRow[]>('/api/providers');
  printTable(
    rows.map((r) => [
      r.id,
      r.name ?? '',
      r.templateId ?? '',
      fmtBool(r.enabled),
      r.modelCount === undefined ? '' : String(r.modelCount),
    ]),
    ['ID', 'NAME', 'TEMPLATE', 'ENABLED', 'MODELS'],
  );
}

export async function runProviderAdd(opts: {
  template: string;
  variant?: string;
  key: string;
  name?: string;
}): Promise<void> {
  const templates = await api<TemplateRow[]>('/api/provider-templates');
  const needle = opts.template.toLowerCase();
  const template = templates.find(
    (t) => t.id.toLowerCase() === needle || t.name?.toLowerCase() === needle,
  );
  if (!template) {
    throw new AstraError(
      `Unknown provider template "${opts.template}".`,
      `Available templates: ${templates.map((t) => t.id).join(', ') || '(none reported)'}`,
    );
  }
  const variants = template.variants ?? [];
  let variantId: string | undefined = opts.variant;
  if (variants.length > 0 && !variantId) {
    throw new AstraError(
      `Template "${template.id}" has variants — pass --variant.`,
      `Variants: ${variants.map((v) => v.id).join(', ')}`,
    );
  }
  if (variantId && variants.length > 0 && !variants.some((v) => v.id === variantId)) {
    throw new AstraError(
      `Unknown variant "${variantId}" for template "${template.id}".`,
      `Variants: ${variants.map((v) => v.id).join(', ')}`,
    );
  }
  if (variants.length === 0) variantId = undefined;
  const created = await api<ProviderRow>('/api/providers', {
    method: 'POST',
    body: {
      templateId: template.id,
      ...(variantId ? { variantId } : {}),
      ...(opts.name ? { name: opts.name } : {}),
      apiKey: opts.key,
    },
  });
  success(`Provider added: ${created.name ?? created.id} (id: ${created.id}).`);
}

export async function runProviderRemove(id: string): Promise<void> {
  try {
    await api(`/api/providers/${encodeURIComponent(id)}`, { method: 'DELETE' });
  } catch (err) {
    if (err instanceof ApiError && err.status === 409) {
      throw new AstraError(
        `Provider "${id}" is still bound to one or more clients.`,
        'Disable the client or bind it to another provider first (`astra client disable <kind>`).',
      );
    }
    throw err;
  }
  success(`Provider "${id}" removed.`);
}

export const KNOWN_CLIENT_KINDS: readonly string[] = CLIENT_KINDS;
