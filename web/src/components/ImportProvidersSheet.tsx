// Import providers from other apps (CC Switch, Alma, Claude Code, Codex, Magpie). Two steps: this sheet previews a
// read-only scan (the server never sends keys, only masks) and then asks the server to re-read the chosen entries.

import { RefreshCw } from 'lucide-react';
import { useEffect, useMemo, useState } from 'react';

import { useImportProviders, useImportSources } from '../api/hooks';
import type { ImportCandidate, ImportSource } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { cn } from '../lib/cn';
import { Alert } from './arc/alert/alert';
import { Checkbox } from './arc/checkbox/checkbox';
import { Badge, Button, SearchField, Spinner } from './ui/controls';
import { errorText, Sheet, useFeedback } from './ui/overlays';

const PROTOCOL_LABEL: Record<string, string> = {
  'openai-chat': 'Chat',
  'openai-responses': 'Responses',
  anthropic: 'Anthropic',
  gemini: 'Gemini',
};

const STATUS_TONE = { new: 'green', same: 'neutral', sameHost: 'orange', off: 'neutral', skip: 'red' } as const;

const selectionKey = (c: ImportCandidate) => `${c.source}\u0000${c.ref}`;
const selectable = (c: ImportCandidate) => c.status !== 'skip' && c.status !== 'same';

function hostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

export function ImportProvidersSheet({ open, onOpenChange }: { open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const sources = useImportSources(open);
  const importMutation = useImportProviders();
  const [query, setQuery] = useState('');
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);

  // Every (re)scan resets the selection to the defaults: only brand-new, enabled entries are ticked.
  useEffect(() => {
    if (!sources.data) return;
    setSelected(new Set(sources.data.flatMap((s) => s.items).filter((c) => c.status === 'new').map(selectionKey)));
  }, [sources.data]);

  useEffect(() => {
    if (open) {
      setQuery('');
      setError(null);
    }
  }, [open]);

  const q = query.trim().toLowerCase();
  const matches = (c: ImportCandidate) =>
    !q || c.name.toLowerCase().includes(q) || c.endpoints.some((e) => e.baseUrl.toLowerCase().includes(q));

  const chosen = useMemo(
    () => (sources.data ?? []).flatMap((s) => s.items).filter((c) => selected.has(selectionKey(c)) && selectable(c)),
    [sources.data, selected],
  );

  const toggle = (c: ImportCandidate, on: boolean) =>
    setSelected((prev) => {
      const next = new Set(prev);
      if (on) next.add(selectionKey(c));
      else next.delete(selectionKey(c));
      return next;
    });

  const submit = () => {
    setError(null);
    importMutation.mutate(
      chosen.map((c) => ({ source: c.source, ref: c.ref })),
      {
        onSuccess: (result) => {
          const skipped = result.skipped.length;
          toast(
            skipped > 0
              ? t('import.doneSkipped', { count: result.added.length, skipped })
              : t('import.done', { count: result.added.length }),
          );
          onOpenChange(false);
        },
        // A 409 means a source changed since the preview: show the server's message and rescan.
        onError: (err) => {
          setError(errorText(err));
          void sources.refetch();
        },
      },
    );
  };

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      width={680}
      title={t('import.title')}
      description={t('import.detail')}
      footer={
        <>
          <Button
            icon={<RefreshCw className="size-3.5" />}
            onClick={() => void sources.refetch()}
            disabled={sources.isFetching || importMutation.isPending}
          >
            {t('import.rescan')}
          </Button>
          <Button
            variant="primary"
            disabled={chosen.length === 0 || importMutation.isPending}
            onClick={submit}
          >
            {t('import.selected', { count: chosen.length })}
          </Button>
        </>
      }
    >
      <div className="grid gap-4">
        <SearchField value={query} onChange={setQuery} placeholder={t('import.search')} />
        {error && (
          <Alert tone="danger" title={t('common.error')}>
            {error}
          </Alert>
        )}
        {sources.isLoading && <Spinner lines={3} />}
        {sources.isError && (
          <Alert tone="danger" title={t('common.error')}>
            {errorText(sources.error)}
          </Alert>
        )}
        <div className="grid max-h-[460px] gap-4 overflow-auto pr-1">
          {sources.data?.map((s) => (
            <SourceSection key={s.id} source={s} matches={matches} selected={selected} onToggle={toggle} filtering={q.length > 0} />
          ))}
        </div>
        <p className="text-[11px] text-[var(--text-muted)]">{t('import.footnote')}</p>
      </div>
    </Sheet>
  );
}

function SourceSection({
  source,
  matches,
  selected,
  onToggle,
  filtering,
}: {
  source: ImportSource;
  matches: (c: ImportCandidate) => boolean;
  selected: Set<string>;
  onToggle: (c: ImportCandidate, on: boolean) => void;
  filtering: boolean;
}) {
  const { t } = useI18n();
  const items = source.items.filter(matches);
  return (
    <section aria-label={source.name}>
      <div className="mb-1.5 flex flex-wrap items-baseline gap-x-2 px-1">
        <h3 className="text-[13px] font-medium">{source.name}</h3>
        {source.path && (
          <span className="min-w-0 truncate font-mono text-[11px] text-[var(--text-muted)]" title={t('import.path', { path: source.path })}>
            {source.path}
          </span>
        )}
      </div>
      {source.error ? (
        <Alert tone="danger" title={t('import.sourceError', { error: source.error })} />
      ) : !source.found ? (
        <p className="px-1 text-[12px] text-[var(--text-muted)]">{t('import.notFound')}</p>
      ) : items.length === 0 ? (
        <p className="px-1 text-[12px] text-[var(--text-muted)]">{filtering ? t('import.noMatch') : t('import.noItems')}</p>
      ) : (
        <ul className="card divide-y divide-[var(--border)]">
          {items.map((c) => (
            <CandidateRow key={selectionKey(c)} c={c} checked={selected.has(selectionKey(c))} onToggle={(on) => onToggle(c, on)} />
          ))}
        </ul>
      )}
    </section>
  );
}

function CandidateRow({ c, checked, onToggle }: { c: ImportCandidate; checked: boolean; onToggle: (on: boolean) => void }) {
  const { t } = useI18n();
  const enabled = selectable(c);
  const hint =
    c.status === 'skip' && c.skipReason
      ? t(`import.skip.${c.skipReason}` as MessageKey)
      : c.status === 'same' && c.existing
        ? t('import.existing', { name: c.existing.name })
        : c.status === 'sameHost' && c.existing
          ? t('import.sameHostHint', { name: c.existing.name })
          : null;
  const protocols = c.endpoints.map((e) => `${PROTOCOL_LABEL[e.protocol] ?? e.protocol} · ${hostOf(e.baseUrl)}`).join(' / ');
  return (
    <li className={cn('flex items-start gap-3 px-4 py-3', !enabled && 'opacity-60')}>
      <Checkbox checked={checked && enabled} disabled={!enabled} onCheckedChange={(state) => onToggle(state === true)} aria-label={c.name} />
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-2">
          <span className="min-w-0 truncate text-[13px] font-medium" title={c.name}>
            {c.name}
          </span>
          <Badge tone={STATUS_TONE[c.status]}>{t(`import.status.${c.status}` as MessageKey)}</Badge>
          {c.fromApp && <span className="text-[11px] text-[var(--text-muted)]">{c.fromApp}</span>}
        </div>
        {protocols && <div className="mt-0.5 truncate font-mono text-[11px] text-[var(--text-muted)]" title={protocols}>{protocols}</div>}
        <div className="mt-0.5 flex flex-wrap gap-x-3 text-[11px] text-[var(--text-muted)]">
          <span className="font-mono">{c.hasKey ? (c.keyMasked ?? '••••') : t('import.noKey')}</span>
          {c.models.length > 0 && <span>{t('import.models', { count: c.models.length })}</span>}
          {c.templateId && <span>{t('import.template', { id: c.templateId })}</span>}
        </div>
        {hint && <div className="mt-1 text-[11px] text-[var(--text-secondary,var(--text-muted))]">{hint}</div>}
        {c.ignoredFields.length > 0 && (
          <div className="mt-1 text-[11px] text-[var(--warning)]">{t('import.ignored', { fields: c.ignoredFields.join(', ') })}</div>
        )}
      </div>
    </li>
  );
}
