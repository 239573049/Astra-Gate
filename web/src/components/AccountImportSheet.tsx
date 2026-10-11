import { useEffect, useMemo, useState } from 'react';

import { useImportAccounts } from '../api/hooks';
import type { AccountImportInfo, BatchImportResult, BatchImportStatus } from '../api/types';
import { Alert } from './arc/alert/alert';
import { Badge, Button, Input, TextArea } from './ui/controls';
import { errorText, Sheet, useFeedback } from './ui/overlays';
import { useI18n, type MessageKey } from '../i18n';

/** batch = many entries of any accepted format; token = one pasted credential; apiKey = keys only. */
export type ImportMode = 'batch' | 'token' | 'apiKey';

const STATUS_LABEL: Record<BatchImportStatus, MessageKey> = {
  imported: 'providers.subscription.importBatchStatus.imported',
  updated: 'providers.subscription.importBatchStatus.updated',
  duplicate: 'providers.subscription.importBatchStatus.duplicate',
  invalid_format: 'providers.subscription.importBatchStatus.invalid_format',
  failed: 'providers.subscription.importBatchStatus.failed',
  ready: 'providers.subscription.importBatchStatus.ready',
};

const STATUS_TONE: Record<BatchImportStatus, 'green' | 'accent' | 'neutral' | 'orange' | 'red'> = {
  imported: 'green',
  updated: 'accent',
  ready: 'accent',
  duplicate: 'neutral',
  invalid_format: 'orange',
  failed: 'red',
};

const TITLE: Record<ImportMode, MessageKey> = {
  batch: 'providers.accounts.import.batchTitle',
  token: 'providers.accounts.import.tokenTitle',
  apiKey: 'providers.accounts.import.apiKeyTitle',
};

/** Splits one line into tokens the way the server does (commas / whitespace / semicolons; direct Copilot tokens stay whole). */
function lineTokens(line: string): number {
  return line
    .split(/[,\s]+/)
    .flatMap((p) => {
      const cleaned = p.replace(/^["']+|["']+$/g, '');
      // Direct Copilot tokens (tid=…;exp=…) contain semicolons themselves.
      return cleaned.startsWith('tid=') || cleaned.startsWith('exp=') ? [cleaned] : cleaned.split(';');
    })
    .filter((p) => p.replace(/^["']+|["']+$/g, '').length > 0).length;
}

/**
 * How many entries the pasted text holds; mirrors the server's CredentialImport.SplitEntries: one JSON document
 * (object, or array of objects / strings) counts as is, otherwise one entry per line — a JSON object line is one
 * entry, other lines split like tokens. Blank and `#` lines are ignored.
 */
export function countImportEntries(text: string): number {
  const trimmed = text.trim();
  if (trimmed.length === 0) return 0;
  if (trimmed.startsWith('{') || trimmed.startsWith('[')) {
    try {
      const doc: unknown = JSON.parse(trimmed);
      if (Array.isArray(doc))
        return doc.filter((x) => (typeof x === 'object' && x !== null) || (typeof x === 'string' && x.trim().length > 0)).length;
      if (typeof doc === 'object' && doc !== null) return 1;
    } catch {
      // not one JSON document: fall through to the line split
    }
  }
  let n = 0;
  for (const line of text.split('\n')) {
    const t = line.trim();
    if (t.length === 0 || t.startsWith('#')) continue;
    n += t.startsWith('{') ? 1 : lineTokens(t);
  }
  return n;
}

/**
 * 统一的账户导入弹窗：粘贴（或批量粘贴）refresh token、setup-token、凭据 JSON、API Key、GitHub token。
 * 「预览」只在服务端做识别与去重（不联网、不写库）；「导入」逐条换取 / 校验后落库，结果逐条列出（只有掩码）。
 */
export function AccountImportSheet({
  providerId,
  info,
  mode,
  providerProxy,
  onClose,
}: {
  providerId: string;
  info: AccountImportInfo;
  /** null = closed. */
  mode: ImportMode | null;
  providerProxy?: string | null;
  onClose: () => void;
}) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const run = useImportAccounts(providerId);
  const [text, setText] = useState('');
  const [proxy, setProxy] = useState('');
  const [result, setResult] = useState<(BatchImportResult & { dryRun: boolean }) | null>(null);
  const count = useMemo(() => countImportEntries(text), [text]);

  useEffect(() => {
    if (mode === null) return;
    setText('');
    setResult(null);
  }, [mode]);

  if (mode === null) return <Sheet open={false} onOpenChange={onClose} title="" />;
  const asApiKey = mode === 'apiKey';
  const formats = asApiKey ? info.formats.filter((f) => f.endsWith('-api-key')) : info.formats;
  const takesRefreshTokens = !asApiKey && info.formats.some((f) => f.includes('refresh-token') || f.endsWith('-json'));
  const label = t(asApiKey ? 'providers.accounts.import.apiKeyLabel' : 'providers.accounts.import.label');

  const submit = (dryRun: boolean) =>
    run.mutate(
      { text, asApiKey: asApiKey || undefined, dryRun: dryRun || undefined, httpProxy: proxy.trim() || undefined },
      {
        onSuccess: (r) => {
          setResult({ ...r, dryRun });
          if (dryRun) return;
          toast(t('providers.accounts.import.summary', { ok: r.imported, dup: r.duplicates, fail: r.failed }), r.failed > 0 ? 'info' : 'success');
          if (r.failed > 0) return;
          // 全部成功：单条模式直接关掉；批量模式清空输入，结果留着给人看。
          if (mode !== 'batch') onClose();
          else setText('');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  return (
    <Sheet open onOpenChange={(o) => !o && onClose()} title={t(TITLE[mode])} description={t('providers.accounts.import.desc')} width={560}>
      <div className="flex flex-col gap-3">
        {formats.length > 0 && (
          <p className="text-[11.5px] text-[var(--text-secondary)]">
            {t('providers.accounts.import.formats', {
              formats: formats.map((f) => t(`providers.accounts.format.${f}` as MessageKey)).join(' · '),
            })}
          </p>
        )}
        <TextArea
          mono
          rows={mode === 'batch' ? 8 : 3}
          value={text}
          onChange={(e) => setText(e.target.value)}
          aria-label={label}
          placeholder={label}
          spellCheck={false}
        />
        <Input
          mono
          label={t('providers.accounts.import.proxy')}
          placeholder={providerProxy || t('providers.accounts.proxyFallbackSystem')}
          value={proxy}
          onChange={(e) => setProxy(e.target.value)}
        />
        {takesRefreshTokens && <Alert tone="info" title={t('providers.accounts.import.rotateWarning')} />}
        <div className="flex items-center justify-between gap-2">
          <span className="text-[11px] text-[var(--text-muted)]">{t('providers.accounts.import.count', { n: count })}</span>
          <div className="flex gap-2">
            {mode === 'batch' && (
              <Button disabled={count === 0} loading={run.isPending && run.variables?.dryRun === true} onClick={() => submit(true)}>
                {t('providers.accounts.import.preview')}
              </Button>
            )}
            <Button
              variant="primary"
              disabled={count === 0 || count > info.maxBatch}
              loading={run.isPending && !run.variables?.dryRun}
              onClick={() => submit(false)}
            >
              {t('providers.accounts.import.run')}
            </Button>
          </div>
        </div>
        {result && (
          <div className="flex flex-col gap-1">
            <div className="text-[12px] font-medium">
              {t(result.dryRun ? 'providers.accounts.import.previewSummary' : 'providers.accounts.import.summary', {
                ok: result.imported,
                dup: result.duplicates,
                fail: result.failed,
              })}
            </div>
            <ul className="card max-h-72 divide-y divide-[var(--border)] overflow-auto">
              {result.items.map((item) => (
                <li key={item.index} className="flex items-start gap-2 px-3 py-1.5 text-[12px]">
                  <span className="w-6 shrink-0 text-[var(--text-muted)]">{item.index}</span>
                  <span className="selectable w-28 shrink-0 truncate font-mono">{item.source}</span>
                  <Badge tone={STATUS_TONE[item.status]}>{t(STATUS_LABEL[item.status])}</Badge>
                  {item.kind && (
                    <span className="shrink-0 text-[11px] text-[var(--text-muted)]">
                      {t(`providers.accounts.credential.${item.kind}` as MessageKey)}
                    </span>
                  )}
                  <span className="selectable min-w-0 flex-1 break-words text-[var(--text-secondary)]">
                    {item.account?.displayName && (item.status === 'imported' || item.status === 'updated')
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
  );
}
