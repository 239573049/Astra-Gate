#!/usr/bin/env node
// Generates src/Astra.Core/Seed/models.json from the models.dev catalog.
//
// Pricing is provider-scoped: every system model gets the official vendor price as its default
// `pricing`, and `provider_pricing[<price_key>]` for the same model at other AI providers
// (price key = models.dev provider id = Astra provider template id / price_key).
// Rules models.dev does not express (Anthropic 1h cache writes, DeepSeek peak windows) are added here.
//
// Usage: node scripts/gen-seed.mjs [path-or-url-to-api.json]

import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import path from "node:path";

const SEED_VERSION = 2;
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const out = path.join(root, "src/Astra.Core/Seed/models.json");
const source = process.argv[2] ?? "https://models.dev/api.json";

/** models.dev provider id → system vendor. Models from these providers become system models. */
const VENDORS = {
  openai: { vendor: "openai" },
  anthropic: { vendor: "anthropic" },
  google: { vendor: "google" },
  xai: { vendor: "xai" },
  deepseek: { vendor: "deepseek" },
  moonshotai: { vendor: "moonshotai" },
  zhipuai: { vendor: "zhipuai" },
  alibaba: { vendor: "alibaba", only: /^(qwen|qwq|qvq)/ },
  minimax: { vendor: "minimax" },
  volcengine: { vendor: "bytedance", only: /^doubao/ },
};

/** Other providers whose prices are recorded per model (they also use their own models.dev id as price key). */
const THIRD_PARTY = [
  "openrouter", "siliconflow", "siliconflow-cn", "volcengine", "alibaba", "alibaba-cn",
  "moonshotai-cn", "zai", "minimax-cn", "google-vertex", "google-vertex-anthropic", "amazon-bedrock", "azure",
];

const EXCLUDE = /embedding|tts|image|veo|lyria|realtime|live|audio|transcri|moderation|whisper|dall-e|deep-research|computer-use|asr|livetranslate|ocr|-mt-|character|evolving|customtools|search|codex-spark/i;

// DeepSeek official: off-peak is half of peak. Peak = 01:00-04:00 and 06:00-10:00 UTC, Mon-Fri,
// excluding Chinese public holidays (add them to exclude_dates). models.dev lists the off-peak price.
// Source: https://api-docs.deepseek.com/quick_start/pricing (checked 2026-10-06).
const DEEPSEEK_WINDOWS = [
  { name: "peak-01-04", timezone: "UTC", start: "01:00", end: "04:00", days: [1, 2, 3, 4, 5], multiplier: 2 },
  { name: "peak-06-10", timezone: "UTC", start: "06:00", end: "10:00", days: [1, 2, 3, 4, 5], multiplier: 2 },
];

function normalize(id) {
  let s = id.trim().replace(/^~/, "").toLowerCase();
  s = s.slice(s.lastIndexOf("/") + 1);
  s = s.replace(/@.*$/, "").replace(/[._:\s]+/g, "-");
  s = s.replace(/-(\d{4}-\d{2}-\d{2}|\d{8}|\d{6}|\d{4})$/, "").replace(/-latest$/, "");
  return s.replace(/^-+|-+$/g, "");
}

const round = (n) => (n == null ? undefined : Number(Number(n).toFixed(6)));

function priceSet(cost, isClaude) {
  const p = {};
  if (cost.input != null) p.input = round(cost.input);
  if (cost.cache_read != null) p.cache_read = round(cost.cache_read);
  if (cost.cache_write != null) p.cache_write_5m = round(cost.cache_write);
  // Anthropic 1-hour cache writes cost 2x base input.
  if (isClaude && cost.input != null && cost.cache_write != null) p.cache_write_1h = round(cost.input * 2);
  if (cost.input_audio != null && cost.input_audio !== cost.input) p.input_audio = round(cost.input_audio);
  if (cost.output != null) p.output = round(cost.output);
  if (cost.reasoning != null && cost.reasoning !== cost.output) p.reasoning = round(cost.reasoning);
  if (cost.output_audio != null && cost.output_audio !== cost.output) p.output_audio = round(cost.output_audio);
  return p;
}

function toPricing(model, providerId) {
  const cost = model.cost;
  if (!cost || cost.input == null || cost.output == null) return null;
  const isClaude = /claude/i.test(model.id);
  const pricing = { currency: "USD", unit: "per_1m_tokens", base: priceSet(cost, isClaude) };

  const tiers = (cost.tiers ?? []).filter((t) => t.tier?.type === "context" && t.tier.size > 0);
  if (tiers.length) {
    pricing.context_tiers = tiers
      .sort((a, b) => a.tier.size - b.tier.size)
      .map((t) => ({
        name: `>${Math.round(t.tier.size / 1000)}K`,
        threshold_input_tokens: t.tier.size,
        mode: "whole",
        output_policy: "highest",
        prices: priceSet(t, isClaude),
      }));
  }

  const modes = model.experimental?.modes ?? {};
  for (const mode of Object.values(modes)) {
    const tier = mode?.provider?.body?.service_tier;
    if (!tier || !mode.cost) continue;
    pricing.service_tiers ??= {};
    pricing.service_tiers[tier] = { prices: priceSet(mode.cost, isClaude) };
  }

  if (providerId === "deepseek") {
    pricing.time_windows = structuredClone(DEEPSEEK_WINDOWS);
    pricing.notes = "Base = off-peak price; peak windows x2 (Mon-Fri UTC). Add Chinese public holidays to exclude_dates. Source: api-docs.deepseek.com/quick_start/pricing (2026-10-06)";
  }
  return pricing;
}

function capabilities(m) {
  const input = m.modalities?.input ?? [];
  return {
    vision: input.includes("image") || undefined,
    tools: m.tool_call || undefined,
    reasoning: m.reasoning || undefined,
    prompt_cache: m.cost?.cache_read != null || undefined,
    audio: input.includes("audio") || undefined,
    pdf: input.includes("pdf") || undefined,
    structured_output: m.structured_output || undefined,
  };
}

const isChatModel = (m) =>
  !m.id.startsWith("~") && !EXCLUDE.test(m.id) && (m.modalities?.output ?? ["text"]).includes("text") && m.cost;

const catalog = source.startsWith("http")
  ? await (await fetch(source)).json()
  : JSON.parse(readFileSync(source, "utf8"));

/** canonical key "<vendorProvider>/<normalized id>" → system model */
const byKey = new Map();
const models = [];

for (const [providerId, cfg] of Object.entries(VENDORS)) {
  const provider = catalog[providerId];
  if (!provider) continue;
  // Undated ids first so dated variants become aliases.
  const entries = Object.values(provider.models).filter(isChatModel).filter((m) => !cfg.only || cfg.only.test(m.id))
    .sort((a, b) => a.id.length - b.id.length || a.id.localeCompare(b.id));
  for (const m of entries) {
    const key = `${providerId}/${normalize(m.id)}`;
    const existing = byKey.get(key);
    if (existing) {
      if (existing.id !== m.id && !existing.aliases.includes(m.id)) existing.aliases.push(m.id);
      continue;
    }
    const pricing = toPricing(m, providerId);
    const model = {
      id: m.id,
      display_name: (m.name ?? m.id).replace(/\s*\(latest\)$/, ""),
      vendor: cfg.vendor,
      family: m.family,
      aliases: [],
      context_window: m.limit?.context,
      max_output_tokens: m.limit?.output,
      capabilities: capabilities(m),
      pricing: pricing ?? undefined,
      provider_pricing: {},
      _vendorProvider: providerId,
    };
    byKey.set(key, model);
    models.push(model);
  }
}

const findSystemModel = (providerId, m) => {
  if (m.canonical_model_id) {
    const [vp, ...rest] = m.canonical_model_id.split("/");
    const hit = byKey.get(`${vp}/${normalize(rest.join("/"))}`);
    if (hit) return hit;
  }
  // Same vendor provider (e.g. a dated duplicate) or a unique normalized-id match across vendors.
  const n = normalize(m.id);
  const matches = models.filter((x) => normalize(x.id) === n || x.aliases.some((a) => normalize(a) === n));
  return matches.length === 1 ? matches[0] : null;
};

for (const providerId of THIRD_PARTY) {
  const provider = catalog[providerId];
  if (!provider) continue;
  for (const m of Object.values(provider.models)) {
    if (!isChatModel(m)) continue;
    const sys = findSystemModel(providerId, m);
    if (!sys || sys._vendorProvider === providerId || sys.provider_pricing[providerId]) continue;
    const pricing = toPricing(m, providerId);
    if (!pricing) continue;
    sys.provider_pricing[providerId] = {
      upstream_model_id: m.id !== sys.id ? m.id : undefined,
      pricing,
    };
  }
}

// Hand-maintained aliases: ids that subscription / coding-plan templates use but models.dev
// never lists under that name. Aliases make ModelIdMatcher auto-link them to the system model,
// so the provider model inherits its context window and price instead of showing "unlinked".
// Each value must be an id already present in this seed.
const EXTRA_ALIASES = {
  // Kimi Code (api.kimi.com/coding) model ids ← moonshotai platform ids.
  "kimi-k3": ["k3"],
  "kimi-k2.7-code": ["kimi-for-coding"],
  "kimi-k2.7-code-highspeed": ["kimi-for-coding-highspeed"],
  // GLM Coding Plan 1M-context variants advertise a "[1m]" suffix on the plain model id.
  "glm-5.3": ["glm-5.3[1m]"],
};

for (const [id, aliases] of Object.entries(EXTRA_ALIASES)) {
  const model = models.find((m) => m.id === id);
  if (!model) throw new Error(`EXTRA_ALIASES references unknown model '${id}'`);
  for (const alias of aliases) if (!model.aliases.includes(alias)) model.aliases.push(alias);
}

for (const m of models) delete m._vendorProvider;
models.sort((a, b) => a.vendor.localeCompare(b.vendor) || a.id.localeCompare(b.id));

const seed = {
  seed_version: SEED_VERSION,
  generated_at: new Date().toISOString().slice(0, 10),
  sources: [
    "https://models.dev/api.json",
    "https://api-docs.deepseek.com/quick_start/pricing",
    "https://docs.anthropic.com/en/docs/build-with-claude/prompt-caching (1h cache write = 2x input)",
  ],
  models,
};

writeFileSync(out, JSON.stringify(seed, null, 1) + "\n");
const priced = models.reduce((n, m) => n + Object.keys(m.provider_pricing).length, 0);
console.log(`wrote ${models.length} models, ${priced} provider-specific prices → ${path.relative(root, out)}`);
