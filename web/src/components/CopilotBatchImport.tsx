import { ListPlus } from 'lucide-react';
import { useMemo, useState } from 'react';

import { useBatchImportCopilotAccounts } from '../api/hooks';
import type { BatchImportResult, BatchImportStatus } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { Badge, Button, TextArea } from './ui/controls';
import { errorText, Sheet, useFeedback } from './ui/overlays';

const STATUS_LABEL: Record<BatchImportStatus, MessageKey> = {
  imported: 'providers.subscription.importBatchStatus.imported',
  duplicate: 'providers.subscription.importBatchStatus.duplicate',
  invalid_format: 'providers.subscription.importBatchStatus.invalid_format',
  failed: 'providers.subscription.importBatchStatus.failed',
};

const STATUS_TONE: Record<BatchImportStatus, 'green' | 'neutral' | 'orange' | 'red'> = {
  imported: 'green',
  duplicate: 'neutral',
  invalid_format: 'orange',
  failed: 'red',
};

/** How many tokens the pasted text holds; mirrors the server's parsing (lines, commas, whitespace, `#` comments). */
export function countBatchTokens(text: string): number {
  let n = 0;
  for (const line of text.split('\n')) {
    const trimmed = line.trim();
    if (trimmed.length === 0 || trimmed.startsWith('#')) continue;
    n += trimmed.split(/[,;\s]+/).filter((p) => p.replace(/^["']+|["']+$/g, '').length > 0).length;
  }
  return n;
}

/**
 * Batch import of GitHub tokens as Copilot accounts: a button that opens a sheet with a multi-line box
 * (one token per line). After the run the sheet lists every token — masked — with its outcome.
 */
export function CopilotBatchImport({ providerId }: { providerId: string }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const batch = useBatchImportCopilotAccounts(providerId);
  const [open, setOpen] = useState(false);
  const [text, setText] = useState('');
  const [result, setResult] = useState<BatchImportResult | null>(null);
  const count = useMemo(() => countBatchTokens(text), [text]);

  const run = () =>
    batch.mutate(text, {
      onSuccess: (r) => {
        setResult(r);
        toast(
          t('providers.subscription.importBatchSummary', { ok: r.imported, dup: r.duplicates, fail: r.failed }),
          r.failed > 0 ? 'info' : 'success',
        );
        // Keep only what did not make it in, so a retry does not resend the successes.
        if (r.failed === 0) setText('');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  return (
    <>
      <Button
        size="sm"
        icon={<ListPlus className="size-3.5" />}
        onClick={() => {
          setResult(null);
          setOpen(true);
        }}
        title={t('providers.subscription.importBatchDesc')}
      >
        {t('providers.subscription.importBatch')}
      </Button>
      <Sheet
        open={open}
        onOpenChange={setOpen}
        title={t('providers.subscription.importBatchTitle')}
        description={t('providers.subscription.importBatchDesc')}
      >
        <div className="flex flex-col gap-3">
          <TextArea
            mono
            rows={8}
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder={t('providers.subscription.importBatchPlaceholder')}
            aria-label={t('providers.subscription.importBatchLabel')}
            spellCheck={false}
          />
          <div className="flex items-center justify-between gap-2">
            <span className="text-[11px] text-[var(--text-muted)]">
              {t('providers.subscription.importBatchCount', { n: count })}
            </span>
            <Button variant="primary" loading={batch.isPending} disabled={count === 0} onClick={run}>
              {t('providers.subscription.importBatchRun')}
            </Button>
          </div>
          {result && (
            <div className="flex flex-col gap-1">
              <div className="text-[12px] font-medium">
                {t('providers.subscription.importBatchSummary', {
                  ok: result.imported,
                  dup: result.duplicates,
                  fail: result.failed,
                })}
              </div>
              <ul className="card divide-y divide-[var(--border)]">
                {result.items.map((item) => (
                  <li key={item.index} className="flex items-start gap-2 px-3 py-2 text-[12px]">
                    <span className="w-6 shrink-0 text-[var(--text-muted)]">{item.index}</span>
                    <span className="selectable w-28 shrink-0 font-mono">{item.source}</span>
                    <Badge tone={STATUS_TONE[item.status]}>{t(STATUS_LABEL[item.status])}</Badge>
                    <span className="selectable min-w-0 flex-1 break-words text-[var(--text-secondary)]">
                      {item.account?.displayName && item.status === 'imported'
                        ? `${item.account.displayName}${item.message ? ` · ${item.message}` : ''}`
                        : (item.message ?? '')}
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      </Sheet>
    </>
  );
}
