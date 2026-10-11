import { Alert } from './arc/alert/alert';
import { Copy, KeyRound } from 'lucide-react';
import { useState } from 'react';
import { useNavigate } from 'react-router';

import { useClaudeDirect, useClaudeDirectRequests, useSelectClaudeDirect } from '../api/hooks';
import type { ClaudeDirectAccount, ClaudeDirectMode, ClaudeDirectRequest } from '../api/types';
import { Badge, Button, EmptyState, Group, Row, Segmented, Spinner, Switch } from './ui/controls';
import { errorText, Select, useFeedback } from './ui/overlays';
import { useI18n } from '../i18n';
import { formatDateTime, formatMs, formatNanos, formatTokens } from '../lib/format';
import { systemActions } from '../shell/systemActions';

type ClaudeDirectRequests = ReturnType<typeof useClaudeDirectRequests>;

/** The launcher command: it takes the account from the server, so it carries no id and no credential. */
const LAUNCH_COMMAND = 'astra claude';

/** Totals of the shown requests: the client's input rows, the whole input, the output, and its own cost estimate. */
export function claudeDirectTotals(rows: ClaudeDirectRequest[]) {
  return rows.reduce(
    (sum, r) => ({
      requests: sum.requests + 1,
      inputTokens: sum.inputTokens + r.inputTokens,
      totalInputTokens: sum.totalInputTokens + r.inputTokens + r.cacheReadTokens + r.cacheCreationTokens,
      outputTokens: sum.outputTokens + r.outputTokens,
      failed: sum.failed + (r.status === 'error' ? 1 : 0),
      estimatedCostNanoUsd: sum.estimatedCostNanoUsd + (r.estimatedCostNanoUsd ?? 0),
    }),
    { requests: 0, inputTokens: 0, totalInputTokens: 0, outputTokens: 0, failed: 0, estimatedCostNanoUsd: 0 },
  );
}

/** Only an active, enabled account may carry a session; the server refuses the rest with a 409. */
const accountUsable = (a: ClaudeDirectAccount) => a.status === 'active' && a.enabled;

/** "Work · me@example.com · Pro", plus the status suffix when the account cannot be used. */
function accountLabel(a: ClaudeDirectAccount, suffix: string): string {
  const head = [a.name || a.id, a.email ? `· ${a.email}` : null, a.plan ? `· ${a.plan}` : null].filter(Boolean).join(' ');
  return accountUsable(a) ? head : `${head} · ${suffix}`;
}

/**
 * The Claude Code panel for the Astra launcher's direct mode: the mode selector, the subscription accounts the
 * launcher may use, the terminal command that starts one, and the opt-in statistics of the selected account.
 * The accounts are the ones already signed in under the Claude subscription provider — nothing is created or
 * signed in here, and the user's own `claude` command and ~/.claude configuration are never touched.
 * `gatewayEnabled` is the client's own take-over switch (ClientsPage): while it is on, direct cannot take effect.
 */
export function ClaudeDirectPanel({ enabled, gatewayEnabled }: { enabled: boolean; gatewayEnabled: boolean }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const navigate = useNavigate();
  const state = useClaudeDirect(enabled);
  const select = useSelectClaudeDirect();
  const [copied, setCopied] = useState<string | null>(null);

  const mode = state.data?.mode ?? 'gateway';
  const accounts = state.data?.accounts ?? [];
  const savedId = state.data?.accountId ?? null;
  const selectedId = savedId ?? accounts.find(accountUsable)?.id ?? null;
  const selected = accounts.find((a) => a.id === selectedId);
  const direct = mode === 'direct';
  // While the client is taken over, any direct selection would be written over by the gateway config.
  const warned = gatewayEnabled && (direct || state.data != null);
  // Statistics are collected only for a selected direct account with consent on; polling follows that.
  const live = direct && selected?.telemetryEnabled === true;
  const requests = useClaudeDirectRequests(live ? selected?.id : null, live);
  const busy = select.isPending;

  // The account id is the server's; the launcher reads it from there, so the command stays account-free.
  const choose = (body: { mode: ClaudeDirectMode; accountId?: string | null; telemetryEnabled?: boolean }) =>
    select.mutate(body, { onError: (e) => toast(errorText(e), 'error') });

  const copy = async (text: string) => {
    if (!(await systemActions.copy(text))) return;
    setCopied(text);
    toast(t('common.copied'));
  };

  const switchMode = (next: ClaudeDirectMode) => {
    if (next === mode || busy || !state.data) return;
    // Direct needs an active account; without one the user is sent to the providers page instead of a dead end.
    const target = selectedId ?? accounts.find(accountUsable)?.id ?? null;
    if (next === 'direct' && !target) {
      toast(t('clients.claudeDirect.needAccountToast'), 'error');
      return;
    }
    choose({ mode: next, accountId: target });
  };

  const chooseAccount = (id: string) => {
    if (busy || id === selectedId) return;
    choose({ mode, accountId: id });
  };

  const setConsent = (on: boolean) => {
    if (!selected || busy) return;
    choose({ mode, accountId: selected.id, telemetryEnabled: on });
  };

  return (
    <>
      <Group title={t('clients.claudeDirect.modeTitle')} footer={t('clients.claudeDirect.modeFooter')}>
        <Row label={t('clients.claudeDirect.mode')} detail={t(direct ? 'clients.claudeDirect.directDetail' : 'clients.claudeDirect.gatewayDetail')}>
          <Segmented
            ariaLabel={t('clients.claudeDirect.mode')}
            value={mode}
            onChange={switchMode}
            items={[
              { value: 'gateway', label: t('clients.claudeDirect.gateway') },
              { value: 'direct', label: t('clients.claudeDirect.direct') },
            ]}
          />
        </Row>
        <p className="px-4 pb-2 text-[11px] text-[var(--text-muted)]">{t('clients.claudeDirect.modeNote')}</p>
      </Group>

      {state.isLoading && <Spinner lines={3} />}
      {state.isError && (
        // The endpoint is loopback-only: a 403 means this console is not on the machine running astra-server.
        <div className="mb-5">
          <Alert tone="danger" title={t('clients.claudeDirect.loadFailed')}>
            {errorText(state.error)}
          </Alert>
        </div>
      )}

      {warned && (
        <div className="mb-5">
          <Alert tone="warning" title={t('clients.claudeDirect.gatewayOnTitle')}>
            {t('clients.claudeDirect.gatewayOnDetail')}
          </Alert>
        </div>
      )}

      {state.data && (
        <Group title={t('clients.claudeDirect.accountsTitle')} footer={t('clients.claudeDirect.accountsFooter')}>
          {accounts.length === 0 ? (
            <EmptyState
              title={t('clients.claudeDirect.noAccount')}
              detail={t('clients.claudeDirect.noAccountDetail')}
              action={
                <Button variant="primary" onClick={() => navigate('/providers')}>
                  {t('clients.claudeDirect.goProviders')}
                </Button>
              }
            />
          ) : (
            <Row label={t('clients.claudeDirect.account')}>
              <Select
                className="max-w-[300px]"
                ariaLabel={t('clients.claudeDirect.account')}
                value={selectedId}
                onChange={chooseAccount}
                options={accounts.map((a) => ({
                  value: a.id,
                  label: accountLabel(a, t('clients.claudeDirect.accountDisabledSuffix', { status: t(`providers.subscription.status.${a.status}` as 'providers.subscription.status.active') })),
                  disabled: !accountUsable(a),
                }))}
              />
            </Row>
          )}
        </Group>
      )}

      {state.data && direct && selected && (
        <Group title={t('clients.claudeDirect.launchTitle')} footer={t('clients.claudeDirect.launchNote')}>
          <LauncherRow command={LAUNCH_COMMAND} copied={copied} onCopy={copy} />
        </Group>
      )}

      {state.data && direct && selected && (
        <Group title={t('clients.claudeDirect.statsTitle')} footer={t('clients.claudeDirect.statsFooter')}>
          <Row label={t('clients.claudeDirect.statsConsent')} detail={t('clients.claudeDirect.statsConsentDetail')}>
            <Switch
              label={t('clients.claudeDirect.statsConsent')}
              checked={selected.telemetryEnabled}
              disabled={busy}
              onChange={setConsent}
            />
          </Row>
          {live && <ClaudeDirectStats requests={requests} />}
        </Group>
      )}
    </>
  );
}

/** The launcher command with its copy button: a fixed public command, never a credential. */
function LauncherRow({ command, copied, onCopy }: { command: string; copied: string | null; onCopy: (text: string) => void }) {
  const { t } = useI18n();
  return (
    <Row label={<span className="font-mono text-[12px] break-all selectable">{command}</span>}>
      <Button
        size="sm"
        icon={<Copy className="size-3" />}
        variant={copied === command ? 'primary' : 'glass'}
        aria-label={t('common.copy')}
        title={t('common.copy')}
        onClick={() => void onCopy(command)}
      >
        {copied === command ? t('common.copied') : t('common.copy')}
      </Button>
    </Row>
  );
}

/** The selected account's reported requests: the totals of what is shown, then one row per request. */
function ClaudeDirectStats({ requests }: { requests: ClaudeDirectRequests }) {
  const { t, locale } = useI18n();
  const rows = requests.data ?? [];
  const totals = claudeDirectTotals(rows);

  if (requests.isLoading) {
    return (
      <div className="p-4">
        <Spinner lines={3} />
      </div>
    );
  }
  if (requests.isError) {
    return (
      <div className="p-4">
        <Alert tone="danger" title={t('clients.claudeDirect.statsFailed')}>
          {errorText(requests.error)}
        </Alert>
      </div>
    );
  }
  if (rows.length === 0) {
    return <EmptyState icon={<KeyRound className="size-6" />} title={t('clients.claudeDirect.statsEmpty')} detail={t('clients.claudeDirect.statsEmptyDetail')} />;
  }

  return (
    <>
      <Row
        label={t('clients.claudeDirect.statsTotals', { count: formatTokens(totals.requests) })}
        detail={t('clients.claudeDirect.statsInDetail', {
          input: formatTokens(totals.totalInputTokens),
          cache: formatTokens(rows.reduce((sum, r) => sum + r.cacheReadTokens, 0)),
          output: formatTokens(totals.outputTokens),
        })}
      />
      <Row label={t('clients.claudeDirect.statsCost')} detail={t('clients.claudeDirect.statsCostDetail')}>
        <span className="num text-[13px]">{formatNanos(rows.some((r) => r.estimatedCostNanoUsd != null) ? totals.estimatedCostNanoUsd : null)}</span>
      </Row>
      {totals.failed > 0 && (
        <Row label={t('clients.claudeDirect.statsFailedCount', { count: formatTokens(totals.failed) })}>
          <Badge tone="red">{t('clients.claudeDirect.statusError')}</Badge>
        </Row>
      )}
      {rows.map((r) => (
        <Row
          key={r.id}
          label={
            <span className="flex items-center gap-2">
              <span className="truncate">{r.model}</span>
              {r.status === 'error' ? (
                <Badge tone="red">{t('clients.claudeDirect.statusError')}</Badge>
              ) : (
                <Badge tone="green">{t('status.success')}</Badge>
              )}
            </span>
          }
          detail={
            <span className="num">
              {[
                formatDateTime(r.occurredAtUtc, locale),
                t('clients.claudeDirect.tokensDetail', { input: formatTokens(r.inputTokens), output: formatTokens(r.outputTokens) }),
                r.durationMs != null ? formatMs(r.durationMs) : null,
                r.accountUuid ?? null,
              ]
                .filter(Boolean)
                .join(' · ')}
            </span>
          }
        >
          <span className="num text-[13px]">{formatNanos(r.estimatedCostNanoUsd)}</span>
        </Row>
      ))}
    </>
  );
}
