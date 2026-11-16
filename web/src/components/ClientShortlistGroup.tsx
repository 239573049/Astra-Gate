import { useEffect, useMemo, useState } from 'react';

import { useClientModels, useSetShortlist } from '../api/hooks';
import type { ClientInfo } from '../api/types';
import { useI18n } from '../i18n';
import { Button, Group, Row, SearchField, Spinner, Switch } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';

/** The shortlist stored in a client's extras (extras.modelShortlist), in its saved order. */
export function shortlistOf(extras: Record<string, unknown> | undefined): string[] {
  const raw = extras?.modelShortlist;
  return Array.isArray(raw) ? raw.filter((m): m is string => typeof m === 'string') : [];
}

/**
 * Which models a client should see (magpie's "visible" list): only the ticked ones reach its model list, none ticked =
 * no limit. Saved straight to the server, which rewrites an enabled client's model list.
 */
export function ClientShortlistGroup({ client }: { client: ClientInfo }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const all = useClientModels(client.kind, true, true);
  const save = useSetShortlist();
  const saved = shortlistOf(client.extras);
  const savedKey = saved.join('\n');
  const [picked, setPicked] = useState<string[]>(saved);
  const [filter, setFilter] = useState('');
  // eslint-disable-next-line react-hooks/exhaustive-deps
  useEffect(() => setPicked(shortlistOf(client.extras)), [client.kind, savedKey]);

  const available = useMemo(() => all.data ?? [], [all.data]);
  // Shortlisted ids the providers no longer serve stay listed (and can be unticked).
  const rows = useMemo(() => [...available, ...picked.filter((m) => !available.includes(m))], [available, picked]);
  const shown = rows.filter((m) => m.toLowerCase().includes(filter.trim().toLowerCase()));
  const changed = picked.join('\n') !== savedKey;
  const toggle = (id: string, on: boolean) => setPicked((cur) => (on ? [...cur, id] : cur.filter((m) => m !== id)));
  const submit = (models: string[]) =>
    save.mutate(
      { kind: client.kind, models },
      { onSuccess: () => toast(t('common.saved'), 'success'), onError: (e) => toast(errorText(e), 'error') },
    );

  return (
    <Group title={t('clients.shortlist.title')} footer={client.enabled ? t('clients.shortlist.footerEnabled') : t('clients.shortlist.footer')}>
      <Row
        label={picked.length === 0 ? t('clients.shortlist.empty') : t('clients.shortlist.summary', { count: picked.length, total: rows.length })}
      >
        {picked.length > 0 && (
          <Button size="sm" onClick={() => setPicked([])}>
            {t('clients.shortlist.clear')}
          </Button>
        )}
        {changed && (
          <Button size="sm" variant="primary" loading={save.isPending} onClick={() => submit(picked)}>
            {t('common.apply')}
          </Button>
        )}
      </Row>
      {all.isLoading && (
        <div className="p-4">
          <Spinner lines={2} />
        </div>
      )}
      {rows.length > 8 && (
        <div className="px-4 py-2">
          <SearchField value={filter} onChange={setFilter} placeholder={t('clients.shortlist.filter')} />
        </div>
      )}
      <div className="max-h-[320px] overflow-y-auto">
        {shown.map((m) => (
          <Row
            key={m}
            label={<span className="font-mono text-[12px]">{m}</span>}
            detail={available.includes(m) ? undefined : t('clients.shortlist.missing')}
          >
            <Switch checked={picked.includes(m)} onChange={(on) => toggle(m, on)} label={m} />
          </Row>
        ))}
      </div>
    </Group>
  );
}
