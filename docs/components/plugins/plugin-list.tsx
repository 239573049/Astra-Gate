import Link from 'next/link';

import { KindBadge, PluginCard, copy, type MarketLang } from '@/components/plugins/market';
import { buildListings, summaryOf } from '@/lib/plugin-market/registry';
import { PLUGIN_KINDS, type PluginKind } from '@/lib/plugin-market/types';

/** The plugin market's list page, in either language: /plugins (zh) and /en/plugins (English). */
export async function PluginListView({
  lang,
  searchParams,
  base,
  docsUrl,
}: {
  lang: MarketLang;
  searchParams: Promise<{ kind?: string; q?: string }>;
  base: string;
  docsUrl: string;
}) {
  const t = copy(lang);
  const { kind: rawKind, q: rawQ } = await searchParams;
  const kind = (PLUGIN_KINDS as readonly string[]).includes(rawKind ?? '') ? (rawKind as PluginKind) : undefined;
  const q = (rawQ ?? '').trim().slice(0, 100);

  const all = await buildListings();
  const needle = q.toLowerCase();
  const plugins = all.filter(
    (p) =>
      (!kind || p.kind === kind) &&
      (!needle ||
        [p.package, p.name, summaryOf(p, 'zh'), summaryOf(p, 'en'), ...(p.tags ?? [])].some((s) => s.toLowerCase().includes(needle))),
  );
  const count = (k?: PluginKind) => all.filter((p) => !k || p.kind === k).length;
  const href = (k?: PluginKind) => {
    const params = new URLSearchParams();
    if (k) params.set('kind', k);
    if (q) params.set('q', q);
    const s = params.toString();
    return s ? `${base}?${s}` : base;
  };

  return (
    <div className="flex flex-1 flex-col px-6 py-24">
      <div className="mx-auto w-full max-w-5xl">
        <p className="text-sm font-medium text-fd-muted-foreground">{t.listEyebrow}</p>
        <h1 className="mt-3 text-4xl font-bold">{t.listTitle}</h1>
        <p className="mt-3 max-w-2xl text-fd-muted-foreground">{t.listIntro}</p>

        <form action={base} className="mt-8 flex flex-wrap items-center gap-3">
          {kind && <input type="hidden" name="kind" value={kind} />}
          <input
            name="q"
            defaultValue={q}
            placeholder={t.searchPlaceholder}
            maxLength={100}
            className="h-10 min-w-[220px] flex-1 rounded-lg border border-fd-border bg-transparent px-3 text-sm outline-none focus:border-fd-primary"
          />
          <button type="submit" className="h-10 rounded-lg bg-fd-primary px-4 text-sm font-medium text-fd-primary-foreground">
            {t.search}
          </button>
        </form>

        <nav className="mt-4 flex flex-wrap gap-2 text-sm" aria-label={t.filterLabel}>
          {([undefined, ...PLUGIN_KINDS] as (PluginKind | undefined)[]).map((k) => (
            <Link
              key={k ?? 'all'}
              href={href(k)}
              className={
                'rounded-full border px-3 py-1 transition-colors ' +
                (k === kind
                  ? 'border-fd-primary bg-fd-primary/10 text-fd-foreground'
                  : 'border-fd-border text-fd-muted-foreground hover:bg-fd-accent/50')
              }
            >
              {k ? t.kind[k] : t.all} {count(k)}
            </Link>
          ))}
        </nav>

        {plugins.length === 0 ? (
          <p className="mt-10 text-fd-muted-foreground">{t.empty}</p>
        ) : (
          <div className="mt-6 grid gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {plugins.map((p) => (
              <PluginCard key={p.package} plugin={p} lang={lang} summary={summaryOf(p, lang)} />
            ))}
          </div>
        )}

        <p className="mt-10 text-xs text-fd-muted-foreground">
          {t.trust}{' '}
          <Link href={docsUrl} className="underline">
            {t.trustLink}
          </Link>
          .
        </p>
      </div>
    </div>
  );
}

export { KindBadge };
