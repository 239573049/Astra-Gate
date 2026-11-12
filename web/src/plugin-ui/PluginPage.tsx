import { useMemo } from 'react';
import { useNavigate, useParams, useSearchParams } from 'react-router';

import { usePluginsV2 } from '../api/hooks';
import type { PluginV2Page, PluginV2Slot } from '../api/types';
import { Page } from '../components/layout/Page';
import { EmptyState, Spinner } from '../components/ui/controls';
import { useI18n } from '../i18n';
import { useContributions } from './contributions';
import { PluginView } from './PluginView';

/** Matches a page route ("", "rules/:id") against the address's rest; the params it binds, or null. */
export function matchRoute(pattern: string, rest: string): Record<string, string> | null {
  const want = pattern.split('/').filter(Boolean);
  const have = rest.split('/').filter(Boolean);
  if (want.length !== have.length) return null;
  const params: Record<string, string> = {};
  for (let i = 0; i < want.length; i++) {
    const w = want[i]!;
    if (w.startsWith(':')) params[w.slice(1)] = decodeURIComponent(have[i]!);
    else if (w !== have[i]) return null;
  }
  return params;
}

/** A v2 plugin's page (ui.pages): /plugins/v2/:plugin/:page/*, its route chosen by the rest of the address. */
export function PluginPage() {
  const { locale, t } = useI18n();
  const { plugin = '', page = '', '*': rest = '' } = useParams();
  const [search, setSearch] = useSearchParams();
  const navigate = useNavigate();
  const list = usePluginsV2(locale);
  const contribution: PluginV2Page | undefined = list.data?.contributions.pages.find(
    (p) => p.plugin === plugin && p.id === page,
  );
  const route = useMemo(() => {
    for (const r of contribution?.routes ?? []) {
      const params = matchRoute(r.path, rest);
      if (params) return { view: r.view, params };
    }
    return null;
  }, [contribution, rest]);
  const query = useMemo(() => Object.fromEntries(search.entries()), [search]);

  if (list.isLoading) return <Spinner lines={4} />;
  if (!contribution || !route)
    return (
      <Page title={t('pluginUi.notFound')}>
        <EmptyState title={t('pluginUi.notFound')} detail={`${plugin}/${page}/${rest}`} />
      </Page>
    );
  return (
    <div className="px-6 py-5">
      <PluginView
        key={`${plugin}/${page}/${rest}`}
        target={{ plugin, view: route.view, params: { ...route.params, path: rest }, query }}
        onQuery={(q) =>
          setSearch(
            Object.fromEntries(
              Object.entries(q)
                .filter(([, v]) => v !== null && v !== undefined)
                .map(([k, v]) => [k, String(v)]),
            ),
            { replace: true },
          )
        }
        onClose={() => navigate(-1)}
      />
    </div>
  );
}

/**
 * A named slot of a host page (design/plugin-v2 03 §8): every running plugin's view for it, each under its title.
 * Takes no space when no plugin fills it.
 */
export function PluginSlot({
  name,
  params,
  compact,
}: {
  name: string;
  params?: Record<string, unknown>;
  compact?: boolean;
}) {
  const contributions = useContributions();
  const slots: PluginV2Slot[] = contributions?.slots.filter((s) => s.slot === name) ?? [];
  if (!slots.length) return null;
  return (
    <div className="flex flex-col gap-4">
      {slots.map((s) => (
        <section
          key={`${s.plugin}/${s.view}`}
          className="flex flex-col gap-2"
          aria-label={s.title ?? s.plugin}
        >
          {!compact && (
            <h3 className="px-1 text-[12px] font-medium text-[var(--text-secondary)]">
              {s.title ?? s.plugin} <span className="text-[var(--text-muted)]">· {s.plugin}</span>
            </h3>
          )}
          <PluginView
            target={{ plugin: s.plugin, view: s.view, params: params ?? {}, slot: s.slot }}
            surface={name === 'tray.panel' ? 'tray' : 'slot'}
          />
        </section>
      ))}
    </div>
  );
}
