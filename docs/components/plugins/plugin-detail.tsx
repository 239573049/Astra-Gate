import Link from 'next/link';

import { InstallPanel } from '@/components/plugins/install-panel';
import { KindBadge, PluginIcon, copy, type MarketLang } from '@/components/plugins/market';
import type { RegistryListing } from '@/lib/plugin-market/types';

function Row({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex gap-4 py-2 text-sm">
      <dt className="w-28 shrink-0 text-fd-muted-foreground">{label}</dt>
      <dd className="min-w-0 break-words">{children}</dd>
    </div>
  );
}

/** One plugin of the market, in either language. The page around it supplies the listing and the metadata. */
export function PluginDetailView({
  plugin,
  lang,
  summary,
  backUrl,
}: {
  plugin: RegistryListing;
  lang: MarketLang;
  summary: string;
  backUrl: string;
}) {
  const t = copy(lang);
  const hosted = plugin.tarballs ?? [];

  return (
    <div className="flex flex-1 flex-col px-6 py-24">
      <div className="mx-auto w-full max-w-3xl">
        <Link href={backUrl} className="text-sm text-fd-muted-foreground hover:text-fd-foreground">
          {t.back}
        </Link>
        <div className="mt-4 flex flex-wrap items-center gap-3">
          <PluginIcon plugin={plugin} size={44} />
          <h1 className="text-3xl font-bold">{plugin.name}</h1>
          <KindBadge kind={plugin.kind} lang={lang} />
          <span className="rounded-full bg-fd-muted px-2.5 py-0.5 text-xs text-fd-muted-foreground">
            {plugin.origin === 'astra' ? t.originAstra : t.originCommunity}
          </span>
        </div>
        <code className="mt-2 block text-sm text-fd-muted-foreground">{plugin.package}</code>
        {summary && <p className="mt-4 text-fd-foreground/90">{summary}</p>}
        {plugin.description && plugin.description !== summary && (
          <p className="mt-2 text-sm text-fd-muted-foreground">{plugin.description}</p>
        )}

        <div className="mt-8">
          <InstallPanel spec={plugin.spec ?? plugin.package} lang={lang} minAstraVersion={plugin.minAstraVersion} />
        </div>

        <dl className="mt-8 divide-y divide-fd-border rounded-xl border border-fd-border px-5">
          {plugin.latest && <Row label={t.latest}>v{plugin.latest}</Row>}
          {plugin.license && <Row label={t.license}>{plugin.license}</Row>}
          {plugin.author && <Row label={t.author}>{plugin.author}</Row>}
          {plugin.providers.length > 0 && <Row label={t.providers}>{plugin.providers.join(', ')}</Row>}
          {plugin.replaces && <Row label={t.replaces}>{plugin.replaces}</Row>}
          {plugin.homepage && (
            <Row label={t.homepage}>
              <a href={plugin.homepage} target="_blank" rel="noreferrer" className="underline">
                {plugin.homepage}
              </a>
            </Row>
          )}
          {plugin.repository && (
            <Row label={t.repository}>
              <a href={plugin.repository} target="_blank" rel="noreferrer" className="underline">
                {plugin.repository}
              </a>
            </Row>
          )}
          {plugin.tags && plugin.tags.length > 0 && <Row label={t.tags}>{plugin.tags.join(', ')}</Row>}
          {!plugin.spec && hosted.length === 0 && (
            <Row label={t.source}>
              <a href={`https://www.npmjs.com/package/${plugin.package}`} target="_blank" rel="noreferrer" className="underline">
                npm
              </a>
            </Row>
          )}
        </dl>

        {hosted.length > 0 && (
          <div className="mt-8">
            <h2 className="text-sm font-medium">{t.hosted}</h2>
            <div className="mt-2 overflow-x-auto rounded-xl border border-fd-border">
              <table className="w-full text-left text-sm">
                <thead className="bg-fd-muted/50 text-fd-muted-foreground">
                  <tr>
                    <th className="px-4 py-2 font-medium">{t.version}</th>
                    <th className="px-4 py-2 font-medium">{t.published}</th>
                    <th className="px-4 py-2 font-medium">{t.size}</th>
                    <th className="px-4 py-2 font-medium">SHA-256</th>
                  </tr>
                </thead>
                <tbody>
                  {hosted.map((v) => (
                    <tr key={v.version} className="border-t border-fd-border">
                      <td className="px-4 py-2">
                        <a href={v.url} className="underline">
                          {v.version}
                        </a>
                      </td>
                      <td className="px-4 py-2">
                        {v.publishedAt ? new Date(v.publishedAt).toLocaleDateString(lang === 'en' ? 'en-US' : 'zh-CN') : '—'}
                      </td>
                      <td className="px-4 py-2">{(v.size / 1024).toFixed(1)} KB</td>
                      <td className="px-4 py-2 font-mono text-xs">{v.sha256.slice(0, 16)}…</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </div>
        )}

        <p className="mt-8 text-xs text-fd-muted-foreground">{t.footer}</p>
      </div>
    </div>
  );
}

export function PluginNotFoundView({ lang, backUrl }: { lang: MarketLang; backUrl: string }) {
  const t = copy(lang);
  return (
    <div className="flex flex-1 flex-col justify-center px-6 py-32">
      <div className="mx-auto w-full max-w-xl text-center">
        <h1 className="text-3xl font-bold">{t.notFoundTitle}</h1>
        <p className="mt-3 text-fd-muted-foreground">{t.notFoundBody}</p>
        <Link href={backUrl} className="mt-6 inline-block text-sm underline">
          {t.backToMarket}
        </Link>
      </div>
    </div>
  );
}
