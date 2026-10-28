#!/usr/bin/env node
// Generates tests/fixtures/ui/default/*.json — the deterministic admin-API data set both visual-regression
// sides replay (web: Playwright page.route; Avalonia: a fake ApiClient). The output is committed; this script only
// exists so the data stays internally consistent and reviewable. Fixed clock, seeded PRNG, no I/O except the writes.
// Usage: node scripts/visual/gen-fixtures.mjs [--check]   (--check exits 1 when a committed file is stale)
import { mkdirSync, writeFileSync, readFileSync, existsSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = dirname(dirname(dirname(fileURLToPath(import.meta.url))));
const outDir = join(root, 'tests', 'fixtures', 'ui', 'default');
const check = process.argv.includes('--check');

/** The frozen "now" of every scene (both sides pin their clock to it; UTC). */
export const NOW = new Date('2026-10-26T12:00:00.000Z');
const iso = (d) => d.toISOString().replace(/\.\d{3}Z$/, 'Z');
const ago = (ms) => iso(new Date(NOW.getTime() - ms));
const MIN = 60_000, HOUR = 3_600_000, DAY = 86_400_000;

function mulberry32(a) {
  return () => {
    a |= 0; a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
const rnd = mulberry32(20261026);
const pick = (arr) => arr[Math.floor(rnd() * arr.length)];
const between = (lo, hi) => Math.round(lo + rnd() * (hi - lo));

const files = {};
const routes = [];
const put = (file, data) => { files[file] = data; };
/** query: only these params must match (extra ones are ignored); omitted = any query. */
const route = (path, file, query, method = 'GET') => routes.push({ method, path, ...(query ? { query } : {}), file });

// ---------------------------------------------------------------- server / shell
put('health.json', { status: 'ok' });
put('version.json', { version: '0.11.7', apiVersion: '1.0' });
put('auth-status.json', { required: false, signedIn: false, remote: false, adminBlocked: false });
put('listen.json', { host: '127.0.0.1', port: 17321, configuredHost: '127.0.0.1', hostFromEnvironment: false, passwordSet: false, networkUrls: [] });
put('autostart.json', { supported: true, enabled: false, mechanism: 'launchd', location: '~/Library/LaunchAgents/io.astra.server.plist', program: '/Users/demo/.astra/server/astra-server', canChange: true });
put('update-status.json', { current: '0.11.7', available: null, lastCheckAt: ago(35 * MIN), notes: null, error: null, channel: 'stable', autoCheck: true, feedConfigured: true });
put('tunnel.json', {
  supported: true, enabled: false, state: 'off', publicUrl: null, error: null, connectedAt: null, mode: 'quick', hostname: null,
  hasToken: false, allowAdmin: false, originUrl: 'http://127.0.0.1:17321',
  cloudflared: { path: null, version: null, downloading: false, progress: null, error: null, asset: null, source: null, installed: false, fromEnvironment: false },
  download: { downloadSource: 'auto', downloadBaseUrl: null, cloudflaredPath: null },
  checks: { passwordSet: false, blocker: null }, log: [],
});
put('settings.json', {
  locale: 'zh', debugBodies: false, bodyRetentionDays: 7, requestRetentionDays: 90,
  effortBudgets: { low: 2048, medium: 8192, high: 24576 }, streamIdleTimeoutSec: 120,
  updateChannel: 'stable', updateAutoCheck: true, requestCompression: 'default', requireGatewayKey: true,
  quotaAutoIntervalMinutes: 30, subscriptionQuotaIntervalMinutes: 30,
  usageTimeZone: null, effectiveUsageTimeZone: 'UTC',
  proxyMode: 'system', proxyUrl: null, proxyUsername: null, hasProxyPassword: false, proxyBypass: null,
  pluginMarketUrl: null, pluginMarketDefaultUrl: 'https://astra-gate.si/api/plugin-market',
  port: 17321, host: '127.0.0.1', dataDir: '/Users/demo/.astra', gatewayBaseUrl: 'http://127.0.0.1:17321',
});

// ---------------------------------------------------------------- providers
const quotaOff = { enabled: false, template: null, suggestedTemplate: null, effectiveTemplate: null, intervalMinutes: null, effectiveIntervalMinutes: 30, timeoutSec: 15, baseUrl: null, params: {}, secrets: [], request: null, extract: null };
const providers = [
  {
    id: 'prov_openai', templateId: 'openai', templateVersion: 1, templateUpdateAvailable: false, name: 'OpenAI', icon: 'openai', category: 'official',
    endpoints: [{ protocol: 'openai-chat', baseUrl: 'https://api.openai.com/v1' }, { protocol: 'openai-responses', baseUrl: 'https://api.openai.com/v1' }],
    preferredUpstreamProtocols: ['openai-responses', 'openai-chat'], authScheme: 'bearer', hasApiKey: true, apiKeyMasked: 'sk-…a1b2',
    extraHeaders: {}, httpProxy: null, priceMultiplier: 1, priceKey: 'openai', adapterId: 'openai', settings: {},
    enabled: true, sortOrder: 0, notes: null, website: 'https://platform.openai.com', modelCount: 4, boundClients: ['codex'],
    createdAt: ago(40 * DAY), updatedAt: ago(2 * DAY), quotaConfig: quotaOff, quota: null, quotaCheckedAtUtc: null,
  },
  {
    id: 'prov_anthropic', templateId: 'anthropic', templateVersion: 1, templateUpdateAvailable: false, name: 'Anthropic', icon: 'anthropic', category: 'official',
    endpoints: [{ protocol: 'anthropic', baseUrl: 'https://api.anthropic.com/v1' }],
    preferredUpstreamProtocols: ['anthropic'], authScheme: 'x-api-key', hasApiKey: true, apiKeyMasked: 'sk-ant-…c3d4',
    extraHeaders: { 'anthropic-version': '2023-06-01' }, httpProxy: null, priceMultiplier: 1, priceKey: 'anthropic', adapterId: 'anthropic', settings: {},
    enabled: true, sortOrder: 1, notes: null, website: 'https://console.anthropic.com', modelCount: 3, boundClients: ['claude-code'],
    createdAt: ago(38 * DAY), updatedAt: ago(1 * DAY),
    quotaConfig: { ...quotaOff, enabled: true, template: 'anthropic', suggestedTemplate: 'anthropic', effectiveTemplate: 'anthropic' },
    quota: {
      fetchedAtUtc: ago(12 * MIN), template: 'anthropic', kind: 'balance', isValid: true, invalidMessage: null, planLabel: null,
      plans: [{ name: null, unit: 'USD', remaining: 42.18, total: 100, used: 57.82, usedPercent: 57.82, resetsAtUtc: null, windowMinutes: null, extra: null }],
      error: null, errorCode: null, errorAtUtc: null, failures: 0,
    },
    quotaCheckedAtUtc: ago(12 * MIN),
  },
];
put('providers.json', providers);
for (const p of providers) { put(`providers/${p.id}.json`, p); }
put('provider-templates.json', [
  { id: 'openai', version: 1, name: 'OpenAI', category: 'official', icon: 'openai', website: 'https://platform.openai.com', apiKeyUrl: 'https://platform.openai.com/api-keys', docsUrl: null, endpoints: providers[0].endpoints, preferredUpstreamProtocols: providers[0].preferredUpstreamProtocols, auth: { scheme: 'bearer' }, authScheme: 'bearer', requiresApiKey: true, models: ['gpt-4.1', 'gpt-4.1-mini', 'o4-mini', 'gpt-5'], priceKey: 'openai', defaultPriceMultiplier: null, variants: null, defaultHeaders: null, keyAlternatives: null },
  { id: 'anthropic', version: 1, name: 'Anthropic', category: 'official', icon: 'anthropic', website: 'https://console.anthropic.com', apiKeyUrl: 'https://console.anthropic.com/settings/keys', docsUrl: null, endpoints: providers[1].endpoints, preferredUpstreamProtocols: ['anthropic'], auth: { scheme: 'x-api-key' }, authScheme: 'x-api-key', requiresApiKey: true, models: ['claude-sonnet-4-5', 'claude-opus-4-1', 'claude-haiku-4-5'], priceKey: 'anthropic', defaultPriceMultiplier: null, variants: null, defaultHeaders: { 'anthropic-version': '2023-06-01' }, keyAlternatives: null },
]);
route('/api/providers', 'providers.json');
route('/api/provider-templates', 'provider-templates.json');
for (const p of providers) route(`/api/providers/${p.id}`, `providers/${p.id}.json`);

// ---------------------------------------------------------------- models
const caps = (o = {}) => ({ vision: true, tools: true, reasoning: false, promptCache: true, audio: false, pdf: false, structuredOutput: true, ...o });
const price = (input, output, cache_read) => ({ currency: 'USD', unit: 'per_1m_tokens', base: { input, output, cache_read }, service_tiers: null, context_tiers: null, time_windows: null, per_request: null, notes: null });
const M = (id, displayName, vendor, family, ctx, out, c, pr, keys) => ({
  id, displayName, vendor, family, aliases: [], contextWindow: ctx, maxOutputTokens: out, capabilities: c, pricing: pr,
  source: 'seed', userModifiedFields: [], enabled: true, providerPriceKeys: keys, updatedAt: ago(20 * DAY),
});
const models = [
  M('gpt-4.1', 'GPT-4.1', 'openai', 'gpt-4', 1047576, 32768, caps(), price(2, 8, 0.5), ['openai']),
  M('gpt-4.1-mini', 'GPT-4.1 mini', 'openai', 'gpt-4', 1047576, 32768, caps(), price(0.4, 1.6, 0.1), ['openai']),
  M('o4-mini', 'o4-mini', 'openai', 'o', 200000, 100000, caps({ reasoning: true }), price(1.1, 4.4, 0.275), ['openai']),
  M('gpt-5', 'GPT-5', 'openai', 'gpt-5', 400000, 128000, caps({ reasoning: true, pdf: true }), price(1.25, 10, 0.125), ['openai']),
  M('claude-sonnet-4-5', 'Claude Sonnet 4.5', 'anthropic', 'claude-sonnet', 200000, 64000, caps({ reasoning: true, pdf: true }), price(3, 15, 0.3), ['anthropic']),
  M('claude-opus-4-1', 'Claude Opus 4.1', 'anthropic', 'claude-opus', 200000, 32000, caps({ reasoning: true, pdf: true }), price(15, 75, 1.5), ['anthropic']),
  M('claude-haiku-4-5', 'Claude Haiku 4.5', 'anthropic', 'claude-haiku', 200000, 64000, caps({ reasoning: true }), price(1, 5, 0.1), ['anthropic']),
  M('gemini-2.5-pro', 'Gemini 2.5 Pro', 'google', 'gemini-2.5', 1048576, 65536, caps({ reasoning: true, audio: true, pdf: true }), price(1.25, 10, 0.31), []),
];
put('models.json', models);
put('price-keys.json', [{ priceKey: 'openai', label: 'OpenAI', templateIds: ['openai'] }, { priceKey: 'anthropic', label: 'Anthropic', templateIds: ['anthropic'] }]);
route('/api/models', 'models.json');
route('/api/price-keys', 'price-keys.json');
for (const m of models) {
  put(`models/${m.id}.json`, { ...m, providerPrices: [], providerOverrides: [] });
  route(`/api/models/${m.id}`, `models/${m.id}.json`);
}
// Provider-side model lists (the provider detail page).
const pms = (p) => models.filter((m) => m.providerPriceKeys.includes(p.priceKey)).slice(0, p.modelCount).map((m, i) => ({
  id: (p.sortOrder + 1) * 100 + i, providerId: p.id, modelId: m.id, systemModelId: m.id, enabled: true, sortOrder: i, overrides: {},
  effective: { displayName: m.displayName, contextWindow: m.contextWindow, maxOutputTokens: m.maxOutputTokens, capabilities: m.capabilities },
  origins: { displayName: 'inherited', contextWindow: 'inherited', maxOutputTokens: 'inherited', capabilities: 'inherited', pricing: 'inherited' },
  pricingSource: 'provider', priceKey: p.priceKey, multiplier: 1, effectivePricing: m.pricing,
}));
for (const p of providers) { put(`providers/${p.id}.models.json`, pms(p)); route(`/api/providers/${p.id}/models`, `providers/${p.id}.models.json`); put(`providers/${p.id}.accounts.json`, []); route(`/api/providers/${p.id}/accounts`, `providers/${p.id}.accounts.json`); }

// ---------------------------------------------------------------- tokens
const stat = (requests, cost, inp, out, cr, cw) => ({
  costUsd: cost, requests, inputTokens: inp, outputTokens: out, totalTokens: inp + out, cacheReadTokens: cr, cacheWriteTokens: cw,
  cacheHitRate: inp ? Math.round((cr / inp) * 1000) / 1000 : null, tps: requests ? 62.4 : null,
});
const noLimits = [];
const tokens = [
  {
    id: 'tok_default', name: 'default', isDefault: true, enabled: true, keyMasked: 'sk-astra-Qx7…9fA', providerId: 'prov_anthropic', accountId: null,
    createdAt: ago(40 * DAY), updatedAt: ago(10 * DAY), lastUsedAt: ago(3 * MIN), clients: ['claude-code', 'codex'],
    today: stat(31, 4.82, 912_000, 118_000, 604_000, 71_000), total: stat(2214, 318.4, 61_200_000, 7_900_000, 40_100_000, 5_000_000),
    limits: null, allowedClients: null, limitUsage: noLimits, recentRequests: 2, pool: null,
  },
  {
    id: 'tok_ci', name: 'ci-bot', isDefault: false, enabled: true, keyMasked: 'sk-astra-Lm3…c1Z', providerId: 'prov_openai', accountId: null,
    createdAt: ago(21 * DAY), updatedAt: ago(5 * DAY), lastUsedAt: ago(48 * MIN), clients: [],
    today: stat(14, 0.63, 201_000, 22_000, 90_000, 0), total: stat(612, 19.7, 9_800_000, 1_100_000, 3_900_000, 0),
    limits: { budgets: [{ metric: 'cost', period: 'day', limit: 5 }, { metric: 'requests', period: 'month', limit: 5000 }], rpm: 60 },
    allowedClients: [],
    limitUsage: [
      { metric: 'cost', period: 'day', limit: 5, used: 0.63, remaining: 4.37, usedPercent: 12.6, exhausted: false, resetsAt: iso(new Date('2026-10-27T00:00:00Z')) },
      { metric: 'requests', period: 'month', limit: 5000, used: 412, remaining: 4588, usedPercent: 8.24, exhausted: false, resetsAt: iso(new Date('2026-11-01T00:00:00Z')) },
    ],
    recentRequests: 0, pool: null,
  },
  {
    id: 'tok_pool', name: 'team-shared', isDefault: false, enabled: true, keyMasked: 'sk-astra-Tn8…p4W', providerId: 'prov_openai', accountId: null,
    createdAt: ago(9 * DAY), updatedAt: ago(1 * DAY), lastUsedAt: ago(2 * HOUR), clients: ['opencode'],
    today: stat(5, 0.91, 88_000, 9_000, 20_000, 0), total: stat(190, 41.2, 4_400_000, 520_000, 1_100_000, 0),
    limits: null, allowedClients: null, limitUsage: noLimits, recentRequests: 0,
    pool: {
      strategy: 'failover', sticky: 'session', failover: true, maxAttempts: 2,
      members: [
        { providerId: 'prov_openai', accountId: null, weight: 1, tier: 0, missing: false, health: { state: 'healthy', until: null, reason: null } },
        { providerId: 'prov_anthropic', accountId: null, weight: 1, tier: 1, missing: false, health: { state: 'healthy', until: null, reason: null } },
      ],
    },
  },
];
put('tokens.json', tokens);
route('/api/tokens', 'tokens.json');
for (const t of tokens) { put(`tokens/${t.id}.json`, t); route(`/api/tokens/${t.id}`, `tokens/${t.id}.json`); }

// ---------------------------------------------------------------- clients
const client = (kind, name, protocol, status, installed, extra = {}) => ({
  kind, name, protocol, availability: 'available', availabilityReason: null, mode: 'switch',
  detection: { installed, configExists: installed, version: installed ? '1.4.2' : null, configPaths: installed ? [`~/.${kind}/config.json`] : [] },
  status, enabled: status !== 'disabled', providerId: null, accountId: null, selectedModel: null, extras: {}, bindings: null,
  appliedAt: status === 'disabled' ? null : ago(3 * DAY), warnings: [], requiresRestart: false, configOutdated: false, tokenId: 'tok_default',
  install: null, options: null, repairs: null, protocols: null, ...extra,
});
put('clients.json', [
  client('claude-code', 'Claude Code', 'anthropic', 'enabled', true, { providerId: 'prov_anthropic', bindings: [{ providerId: 'prov_anthropic', accountId: null }], selectedModel: 'claude-sonnet-4-5' }),
  client('codex', 'Codex', 'openai-responses', 'enabled', true, { providerId: 'prov_openai', bindings: [{ providerId: 'prov_openai', accountId: null }], selectedModel: 'gpt-5' }),
  client('opencode', 'OpenCode', 'openai-chat', 'drifted', true, { providerId: 'prov_openai', bindings: [{ providerId: 'prov_openai', accountId: null }], tokenId: 'tok_pool' }),
  client('gemini-cli', 'Gemini CLI', 'gemini', 'disabled', false),
]);
route('/api/clients', 'clients.json');

// ---------------------------------------------------------------- requests (the last ~2 h, newest first)
const reqModels = [
  ['claude-sonnet-4-5', 'prov_anthropic', 'Anthropic', 'anthropic', 'anthropic', 'claude-code', 'tok_default', 'default'],
  ['claude-sonnet-4-5', 'prov_anthropic', 'Anthropic', 'anthropic', 'anthropic', 'claude-code', 'tok_default', 'default'],
  ['claude-haiku-4-5', 'prov_anthropic', 'Anthropic', 'anthropic', 'anthropic', 'claude-code', 'tok_default', 'default'],
  ['gpt-5', 'prov_openai', 'OpenAI', 'openai-responses', 'openai-responses', 'codex', 'tok_default', 'default'],
  ['gpt-4.1-mini', 'prov_openai', 'OpenAI', 'openai-chat', 'openai-chat', null, 'tok_ci', 'ci-bot'],
  ['o4-mini', 'prov_openai', 'OpenAI', 'openai-chat', 'openai-responses', 'opencode', 'tok_pool', 'team-shared'],
];
const priceOf = Object.fromEntries(models.map((m) => [m.id, m.pricing.base]));
const requests = [];
let t = NOW.getTime() - 3 * MIN;
for (let i = 0; i < 50; i++) {
  const [model, pid, pname, inbound, upstream, ck, tid, tname] = pick(reqModels);
  const roll = rnd();
  const status = roll < 0.84 ? 'success' : roll < 0.92 ? 'upstream_error' : roll < 0.96 ? 'client_cancelled' : 'gateway_error';
  const ok = status === 'success';
  const inTok = between(800, 42_000), outTok = ok ? between(60, 4200) : 0;
  const cacheRead = rnd() < 0.6 ? Math.floor(inTok * (0.3 + rnd() * 0.6)) : 0;
  const cacheWrite = rnd() < 0.2 ? between(500, 4000) : 0;
  const p = priceOf[model];
  const cost = ok ? Math.round(((inTok - cacheRead) * p.input + cacheRead * p.cache_read + outTok * p.output) / 1e6 * 1e9) : 0;
  const total = ok ? between(900, 24_000) : between(200, 3000);
  requests.push({
    id: `req_${String(50 - i).padStart(4, '0')}`, startedAtUtc: iso(new Date(t)), clientKind: ck, providerId: pid, providerName: pname,
    inboundProtocol: inbound, upstreamProtocol: upstream, passthrough: inbound === upstream, requestedModel: model, upstreamModel: model,
    systemModelId: model, stream: rnd() < 0.8, responseModel: model, status,
    httpStatus: ok ? 200 : status === 'upstream_error' ? pick([429, 500, 529]) : status === 'gateway_error' ? 502 : null,
    ttftMs: ok ? between(240, 2400) : null, totalMs: total, outputTps: ok ? Math.round((outTok / (total / 1000)) * 10) / 10 : null,
    reasoningEffort: model === 'o4-mini' ? 'medium' : null, reasoningMode: null, reasoningBudgetTokens: null,
    totalInputTokens: ok ? inTok : 0, totalOutputTokens: outTok, cacheReadTokens: ok ? cacheRead : 0, cacheWriteTokens: ok ? cacheWrite : 0,
    reasoningTokens: model === 'o4-mini' && ok ? between(50, 900) : 0, costNanoUsd: cost, usageSource: ok ? 'reported' : 'missing',
    tokenId: tid, tokenName: tname, accountId: null, accountName: null, remote: false, clientIp: null, sessionKey: null,
    stage: null, turnId: null, callKind: null, parentRequestId: null, phases: null, routing: null,
  });
  t -= between(40, 260) * 1000;
}
put('requests.json', { items: requests, total: requests.length, page: 1, pageSize: 50, totalCostNanoUsd: requests.reduce((s, r) => s + r.costNanoUsd, 0) });
route('/api/requests', 'requests.json');
for (const r of requests.slice(0, 3)) {
  put(`requests/${r.id}.json`, { ...r, usageItems: [], error: null });
  route(`/api/requests/${r.id}`, `requests/${r.id}.json`);
}

// ---------------------------------------------------------------- stats
const RANGES = { today: { cost: 6.36, req: 50, inp: 1_201_000, out: 149_000, cr: 714_000, cw: 71_000 }, '7d': { cost: 38.9, req: 402, inp: 9_800_000, out: 1_240_000, cr: 5_900_000, cw: 520_000 }, '30d': { cost: 171.3, req: 1738, inp: 41_000_000, out: 5_300_000, cr: 24_800_000, cw: 2_100_000 } };
for (const [range, v] of Object.entries(RANGES)) {
  put(`stats/summary.${range}.json`, { range, costUsd: v.cost, requests: v.req, successRate: 0.912, inputTokens: v.inp, outputTokens: v.out, cacheReadTokens: v.cr, cacheWriteTokens: v.cw, reasoningTokens: Math.round(v.out * 0.12), avgTtftMs: 1180 });
  route('/api/stats/summary', `stats/summary.${range}.json`, { range });
  put(`stats/top-models.${range}.json`, models.slice(0, 6).map((m, i) => ({ model: m.id, costUsd: Math.round(v.cost * [0.34, 0.24, 0.16, 0.12, 0.09, 0.05][i] * 100) / 100, requests: Math.round(v.req * [0.3, 0.22, 0.17, 0.14, 0.1, 0.07][i]), tokens: Math.round((v.inp + v.out) * [0.3, 0.22, 0.17, 0.14, 0.1, 0.07][i]), inputTokens: Math.round(v.inp * [0.3, 0.22, 0.17, 0.14, 0.1, 0.07][i]), cacheReadTokens: Math.round(v.cr * [0.3, 0.22, 0.17, 0.14, 0.1, 0.07][i]) })));
  route('/api/stats/top-models', `stats/top-models.${range}.json`, { range });
  put(`stats/providers.${range}.json`, [
    { providerId: 'prov_anthropic', name: 'Anthropic', requests: Math.round(v.req * 0.58), successRequests: Math.round(v.req * 0.58 * 0.93), successRate: 0.93, costUsd: Math.round(v.cost * 0.7 * 100) / 100, inputTokens: Math.round(v.inp * 0.6), outputTokens: Math.round(v.out * 0.6), avgTtftMs: 1320, failoversOut: 0, failoversIn: 1 },
    { providerId: 'prov_openai', name: 'OpenAI', requests: Math.round(v.req * 0.42), successRequests: Math.round(v.req * 0.42 * 0.89), successRate: 0.89, costUsd: Math.round(v.cost * 0.3 * 100) / 100, inputTokens: Math.round(v.inp * 0.4), outputTokens: Math.round(v.out * 0.4), avgTtftMs: 980, failoversOut: 1, failoversIn: 0 },
  ]);
  route('/api/stats/providers', `stats/providers.${range}.json`, { range });
  // timeseries: hourly for today, daily otherwise
  const hourly = range === 'today';
  const n = hourly ? 13 : range === '7d' ? 7 : 30;
  for (const by of ['provider', 'token']) {
    const keys = by === 'provider' ? ['prov_anthropic', 'prov_openai'] : ['tok_default', 'tok_ci', 'tok_pool'];
    const pts = [];
    for (let i = n - 1; i >= 0; i--) {
      const d = new Date(NOW.getTime() - i * (hourly ? HOUR : DAY));
      const bucket = hourly ? d.toISOString().slice(0, 13) + ':00' : d.toISOString().slice(0, 10);
      keys.forEach((key, k) => {
        const reqs = Math.max(1, Math.round((v.req / n) * [0.6, 0.3, 0.1][k] * (0.5 + rnd())));
        const costUsd = Math.round(((v.cost / n) * [0.7, 0.25, 0.05][k] * (0.5 + rnd())) * 100) / 100;
        pts.push({ bucket, key, costUsd, requests: reqs, tokens: reqs * between(8000, 24_000) });
      });
    }
    put(`stats/timeseries.${range}.${by}.json`, pts);
    route('/api/stats/timeseries', `stats/timeseries.${range}.${by}.json`, { range, by });
  }
}
const heat = [];
for (let i = 0; i < 365; i++) {
  const d = new Date(NOW.getTime() - i * DAY);
  const wk = d.getUTCDay();
  if (rnd() < (wk === 0 || wk === 6 ? 0.7 : 0.15)) continue;
  heat.push({ day: d.toISOString().slice(0, 10), requests: between(3, wk === 0 || wk === 6 ? 40 : 160) });
}
put('stats/activity-heatmap.json', heat.reverse());
route('/api/stats/activity-heatmap', 'stats/activity-heatmap.json');
put('stats/rate.json', { rpm: 3.4, tpm: 61_800, cacheHitRate: 0.59, windowSeconds: 60 });
route('/api/stats/rate', 'stats/rate.json');

// shell-level singletons
for (const [path, file] of [['/api/health', 'health.json'], ['/api/version', 'version.json'], ['/api/auth/status', 'auth-status.json'], ['/api/settings', 'settings.json'], ['/api/update/status', 'update-status.json'], ['/api/tunnel', 'tunnel.json'], ['/api/server/listen', 'listen.json'], ['/api/server/autostart', 'autostart.json']]) route(path, file);

put('index.json', {
  $comment: 'Route table: first matching entry wins; "query" params must all equal the request\'s (extra params ignored). See tests/fixtures/ui/README.md.',
  now: iso(NOW), timeZone: 'UTC', locale: 'zh', routes,
});

// ---------------------------------------------------------------- write
let stale = 0;
for (const [file, data] of Object.entries(files)) {
  const path = join(outDir, file);
  const text = JSON.stringify(data, null, 2) + '\n';
  if (check) {
    if (!existsSync(path) || readFileSync(path, 'utf8') !== text) { console.error(`stale: ${file}`); stale++; }
  } else {
    mkdirSync(dirname(path), { recursive: true });
    writeFileSync(path, text);
  }
}
if (check) { if (stale) process.exit(1); console.log('fixtures up to date'); }
else console.log(`wrote ${Object.keys(files).length} files to ${outDir}`);
