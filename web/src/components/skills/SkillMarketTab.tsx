import { Download } from 'lucide-react';
import { useMemo, useState } from 'react';

import { useInstallMarketSkill, useSetSkillMarketSource, useSkillMarket, useSkillMarketSource } from '../../api/hooks';
import type { SkillLibrary, SkillMarketEntry } from '../../api/types';
import { useI18n } from '../../i18n';
import { Alert } from '../arc/alert/alert';
import { Checkbox } from '../arc/checkbox/checkbox';
import { Badge, Button, EmptyState, Field, Input, Row, SearchField, Spinner } from '../ui/controls';
import { errorText, useFeedback } from '../ui/overlays';
import { clientName } from './clientNames';

/**
 * The Skills market: search a registry (skills.sh by default, a third-party service — the address is a setting, so a company can point it at its own),
 * results grouped by the repository they come from, and install one from its GitHub repository like any Git source.
 */
export function SkillMarketTab({ library }: { library: SkillLibrary }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const [query, setQuery] = useState('');
  const [clients, setClients] = useState<Set<string>>(new Set());
  const market = useSkillMarket(query);
  const install = useInstallMarketSkill();
  const source = useSkillMarketSource();
  const setSource = useSetSkillMarketSource();
  const [registry, setRegistry] = useState<string | null>(null);

  const groups = useMemo(() => {
    const map = new Map<string, SkillMarketEntry[]>();
    for (const s of market.data?.skills ?? []) map.set(s.source, [...(map.get(s.source) ?? []), s]);
    return [...map.entries()];
  }, [market.data]);

  const detected = library.targets.filter((x) => x.supported && x.detected);
  const run = (entry: SkillMarketEntry) =>
    install.mutate(
      { source: entry.source, skillId: entry.skillId, clients: [...clients] },
      {
        onSuccess: (outcomes) => {
          const failed = outcomes.filter((o) => !o.ok);
          if (failed.length === 0) toast(t('skills.toast.installed', { n: outcomes.length }), 'success');
          else toast(t('skills.toast.partial', { ok: outcomes.length - failed.length, failed: failed.length, reason: failed[0]?.reason ?? '' }), 'error');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  const saveRegistry = (url: string) =>
    setSource.mutate(url, {
      onSuccess: () => setRegistry(null),
      onError: (e) => toast(errorText(e), 'error'),
    });

  return (
    <>
      <Alert tone="info" title={t('skills.market.thirdParty', { url: source.data?.url ?? '' })} />
      <div className="my-3 flex flex-wrap items-center gap-2">
        <SearchField className="w-72" value={query} onChange={setQuery} placeholder={t('skills.market.search')} />
        <Button size="sm" onClick={() => setRegistry(source.data?.url ?? '')} disabled={!library.canCreate}>
          {t('skills.market.registry')}
        </Button>
      </div>
      {registry !== null && (
        <div className="card mb-3 grid gap-3 p-4">
          <Field label={t('skills.market.registry')} hint={t('skills.market.registryHint', { url: source.data?.default ?? '' })}>
            <Input mono value={registry} onChange={(e) => setRegistry(e.target.value)} />
          </Field>
          <div className="flex gap-2">
            <Button variant="primary" loading={setSource.isPending} onClick={() => saveRegistry(registry.trim())}>
              {t('common.save')}
            </Button>
            <Button onClick={() => saveRegistry('')}>{t('skills.market.registryReset')}</Button>
            <Button onClick={() => setRegistry(null)}>{t('common.cancel')}</Button>
          </div>
        </div>
      )}
      {detected.length > 0 && (
        <div className="mb-3 flex flex-wrap items-center gap-x-4 gap-y-2 px-1">
          <span className="text-[12px] text-[var(--text-secondary)]">{t('skills.install.apply')}</span>
          {detected.map((x) => (
            <Checkbox
              key={x.kind}
              label={clientName(x.kind)}
              checked={clients.has(x.kind)}
              onCheckedChange={(s) =>
                setClients((cur) => {
                  const next = new Set(cur);
                  if (s === true) next.add(x.kind);
                  else next.delete(x.kind);
                  return next;
                })
              }
            />
          ))}
        </div>
      )}
      {query.trim().length < 2 && <EmptyState title={t('skills.market.hint')} />}
      {market.isFetching && <Spinner lines={2} />}
      {market.data?.error && <Alert tone="warning" title={`${t('skills.market.error')}: ${market.data.error}`} />}
      {market.data && !market.data.error && groups.length === 0 && !market.isFetching && <EmptyState title={t('skills.market.none')} />}
      {groups.map(([repo, entries]) => (
        <div key={repo} className="mb-3">
          <div className="mb-1 px-1 font-mono text-[12px] text-[var(--text-secondary)]">{repo}</div>
          <div className="card divide-y divide-[var(--border)]">
            {entries.map((e) => (
              <Row
                key={e.skillId}
                label={
                  <span className="flex items-center gap-2">
                    <span className="font-medium">{e.name}</span>
                    {e.installed && <Badge tone="green">{t('skills.market.installed')}</Badge>}
                  </span>
                }
                detail={e.installs > 0 ? t('skills.market.installs', { n: e.installs }) : undefined}
              >
                <Button
                  size="sm"
                  icon={<Download className="size-3.5" />}
                  disabled={e.installed || !library.canCreate || install.isPending}
                  onClick={() => run(e)}
                >
                  {t('skills.market.install')}
                </Button>
              </Row>
            ))}
          </div>
        </div>
      ))}
    </>
  );
}
