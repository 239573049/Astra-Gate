import { PLUGIN_KINDS, type EntryMeta, type PluginKind } from './types';

const MAX_TEXT = 500;

function text(value: unknown, max = MAX_TEXT): string | undefined {
  return typeof value === 'string' && value.trim().length > 0 ? value.trim().slice(0, max) : undefined;
}

/** An icon name, or an https URL — the same rule as PluginService.SafeIcon on the client (no data: / svg). */
function icon(value: unknown): string | undefined {
  const v = text(value, 2048);
  if (!v) return undefined;
  if (/^https:\/\/\S+$/i.test(v)) return v;
  return v.length <= 64 && /^[A-Za-z0-9_-]+$/.test(v) ? v : undefined;
}

function url(value: unknown): string | undefined {
  const v = text(value, 2048);
  return v && /^https?:\/\/\S+$/i.test(v) ? v : undefined;
}

function stringList(value: unknown, maxItems: number, maxLen: number): string[] | undefined {
  if (!Array.isArray(value)) return undefined;
  const items = value.map((v) => text(v, maxLen)).filter((v): v is string => !!v).slice(0, maxItems);
  return items.length > 0 ? items : undefined;
}

function isMinVersion(v: string): boolean {
  return /^\d+(\.\d+){0,2}$/.test(v);
}

/** Validates caller-supplied metadata (the upload header or an entries POST body): unknown keys are dropped. */
export function cleanMeta(raw: unknown): EntryMeta {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const o = raw as Record<string, unknown>;
  const meta: EntryMeta = {};
  const name = text(o.name, 100);
  if (name) meta.name = name;
  const ic = icon(o.icon);
  if (ic) meta.icon = ic;
  if (typeof o.kind === 'string' && (PLUGIN_KINDS as readonly string[]).includes(o.kind)) meta.kind = o.kind as PluginKind;
  const providers = stringList(o.providers, 20, 100);
  if (providers) meta.providers = providers;
  const replaces = text(o.replaces, 100);
  if (replaces) meta.replaces = replaces;
  if (o.summary && typeof o.summary === 'object' && !Array.isArray(o.summary)) {
    const summary: Record<string, string> = {};
    for (const [lang, v] of Object.entries(o.summary as Record<string, unknown>)) {
      const t = text(v, 600);
      if (t && /^[a-z]{2}(-[A-Za-z]{2,4})?$/.test(lang)) summary[lang] = t;
    }
    if (Object.keys(summary).length > 0) meta.summary = summary;
  }
  const spec = text(o.spec, 300);
  if (spec && /^(github:)[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+(#\S+)?$/.test(spec)) meta.spec = spec;
  const description = text(o.description, 1000);
  if (description) meta.description = description;
  const license = text(o.license, 100);
  if (license) meta.license = license;
  const author = text(o.author, 200);
  if (author) meta.author = author;
  const homepage = url(o.homepage);
  if (homepage) meta.homepage = homepage;
  const repository = url(o.repository);
  if (repository) meta.repository = repository;
  const tags = stringList(o.tags, 12, 40);
  if (tags) meta.tags = tags;
  const min = text(o.minAstraVersion, 32);
  if (min && isMinVersion(min)) meta.minAstraVersion = min;
  return meta;
}

function normalizeRepo(value: unknown): string | undefined {
  const raw = typeof value === 'string' ? value : value && typeof value === 'object' ? (value as { url?: unknown }).url : undefined;
  if (typeof raw !== 'string') return undefined;
  const cleaned = raw.trim().replace(/^git\+/, '').replace(/\.git$/, '').replace(/^git:\/\//, 'https://');
  const gh = /^github:([\w.-]+\/[\w.-]+)$/.exec(cleaned);
  return url(gh ? `https://github.com/${gh[1]}` : cleaned);
}

/** What a package.json says that the market can show; `kind` follows PackageManifest on the client. */
export function metaFromPackageJson(pkg: Record<string, unknown>): EntryMeta {
  const magpie = pkg.magpie && typeof pkg.magpie === 'object' ? (pkg.magpie as Record<string, unknown>) : {};
  const author = typeof pkg.author === 'string' ? pkg.author : (pkg.author as { name?: unknown } | undefined)?.name;
  const license = typeof pkg.license === 'string' ? pkg.license : (pkg.license as { type?: unknown } | undefined)?.type;
  const keywords = Array.isArray(pkg.keywords) ? pkg.keywords : [];
  const piAgent = '@earendil-works/pi-coding-agent';
  const deps = { ...(pkg.dependencies as object), ...(pkg.peerDependencies as object) } as Record<string, unknown>;
  let kind: PluginKind = 'provider';
  if (typeof magpie.agent === 'string') kind = 'agent';
  else if (typeof magpie.middleware === 'string') kind = 'middleware';
  else if (pkg.pi && typeof pkg.pi === 'object') kind = 'pi';
  else if (keywords.includes('pi-package') || piAgent in deps) kind = 'pi';
  return cleanMeta({
    kind,
    icon: magpie.icon,
    description: pkg.description,
    license,
    author,
    homepage: pkg.homepage,
    repository: normalizeRepo(pkg.repository),
    tags: keywords.filter((k) => typeof k === 'string' && !k.startsWith('magpie-') && k !== 'pi-package' && k !== 'opencode'),
  });
}

/** The kind a set of npm keywords / GitHub topics says a plugin is (PluginMarket.KindOfKeywords). */
export function kindOfKeywords(keywords: unknown): PluginKind {
  const words = new Set(Array.isArray(keywords) ? keywords.filter((k): k is string => typeof k === 'string') : []);
  if (words.has('magpie-middleware')) return 'middleware';
  if (words.has('magpie-agent')) return 'agent';
  if (words.has('pi-package')) return 'pi';
  return 'provider';
}
