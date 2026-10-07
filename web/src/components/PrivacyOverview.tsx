import { ShieldCheck } from 'lucide-react';
import { useState } from 'react';

import { usePrivacyEventStats } from '../api/hooks';
import type { Range } from '../api/types';
import { MetricCard } from './arc/metric-card/metric-card';
import { SortableDataTable, type DataColumn } from './arc/sortable-data-table/sortable-data-table';
import { Alert } from './arc/alert/alert';
import { Segmented, SectionTitle, Spinner } from './ui/controls';
import { errorText } from './ui/overlays';
import { useI18n } from '../i18n';
import { PrivacyPolicyGroup } from './PrivacyGuardSettings';
import { usePrivacyCategoryLabel } from './PrivacyMeta';

const RANGES: Range[] = ['today', '7d', '30d', 'all'];

type StatRow = {
  category: string;
  requests: number;
  hits: number;
  warnHits: number;
  blockHits: number;
  redactHits: number;
};

/** 隐私护栏概览：防护统计 + 类别命中 + 策略开关。 */
export function PrivacyOverview() {
  const { t } = useI18n();
  const categoryLabel = usePrivacyCategoryLabel();
  const [range, setRange] = useState<Range>('7d');
  const stats = usePrivacyEventStats(range);
  const s = stats.data;

  const columns: DataColumn<StatRow>[] = [
    { key: 'category', label: t('privacy.stats.col.category'), render: (v) => categoryLabel(String(v)) },
    { key: 'requests', label: t('privacy.stats.col.requests'), numeric: true, width: 110 },
    { key: 'hits', label: t('privacy.stats.col.hits'), numeric: true, width: 96 },
    { key: 'warnHits', label: t('privacy.stats.col.warn'), numeric: true, width: 84 },
    { key: 'blockHits', label: t('privacy.stats.col.block'), numeric: true, width: 84 },
    { key: 'redactHits', label: t('privacy.stats.col.redact'), numeric: true, width: 84 },
  ];
  const rows: StatRow[] = (s?.categories ?? []).map((c) => ({ ...c }));

  return (
    <div className="flex flex-col gap-4">
      <section>
        <SectionTitle
          action={
            <Segmented
              ariaLabel={t('overview.range')}
              value={range}
              onChange={setRange}
              items={RANGES.map((r) => ({ value: r, label: t(`range.${r}`) }))}
            />
          }
        >
          {t('privacy.stats')}
        </SectionTitle>
        {stats.isLoading && <Spinner lines={3} className="py-4" />}
        {stats.isError && <Alert tone="danger" title={t('common.error')}>{errorText(stats.error)}</Alert>}
        {s && (
          <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">
            <MetricCard label={t('privacy.stats.events')} value={s.events} context={t(`range.${range}`)} />
            <MetricCard label={t('privacy.stats.blocked')} value={s.blocked} context={t(`range.${range}`)} />
            <MetricCard label={t('privacy.stats.redactions')} value={s.redactions} context={t(`range.${range}`)} />
            <MetricCard label={t('privacy.stats.dryRun')} value={s.dryRun} context={t(`range.${range}`)} />
          </div>
        )}
      </section>

      <section>
        <SectionTitle>{t('privacy.stats.byCategory')}</SectionTitle>
        {s && s.categories.length === 0 && (
          <div className="card">
            <EmptyHint />
          </div>
        )}
        {s && s.categories.length > 0 && (
          <SortableDataTable<StatRow>
            caption={t('privacy.stats.byCategory')}
            rows={rows}
            columns={columns}
            rowKey="category"
            defaultSort={{ key: 'hits', direction: 'desc' }}
          />
        )}
      </section>

      <div className="mx-auto w-full max-w-[720px]">
        <PrivacyPolicyGroup />
      </div>
    </div>
  );
}

function EmptyHint() {
  const { t } = useI18n();
  return (
    <div className="flex flex-col items-center gap-2 px-4 py-8 text-center">
      <ShieldCheck className="size-6 text-[var(--text-muted)]" />
      <div className="text-[13px]">{t('privacy.logs.empty')}</div>
      <div className="text-[12px] text-[var(--text-secondary)]">{t('privacy.logs.empty.detail')}</div>
    </div>
  );
}
