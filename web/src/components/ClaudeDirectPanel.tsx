import { Alert } from './arc/alert/alert';
import { Copy, KeyRound, Plus } from 'lucide-react';
import { useState } from 'react';

import { useClaudeDirect, useClaudeDirectRequests, useCreateClaudeProfile, useSelectClaudeDirect } from '../api/hooks';
import type { ClaudeDirectMode, ClaudeDirectRequest } from '../api/types';
import { Badge, Button, EmptyState, Group, Input, Row, Segmented, Spinner, Switch } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';
import { useI18n } from '../i18n';
import { formatDateTime, formatMs, formatNanos, formatTokens } from '../lib/format';
import { systemActions } from '../shell/systemActions';

type ClaudeDirectRequests = ReturnType<typeof useClaudeDirectRequests>;

/** One command of the Astra launcher; the profile id is the only variable part. */
function launcherCommand(kind: 'login' | 'status' | 'run', id: string): string {
  const tail = kind === 'login' ? ' --login' : kind === 'status' ? ' --status' : '';
  return `astra claude --profile ${id}${tail}`;
}

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

/**
 * The Claude Code panel for the Astra launcher's direct mode: the mode selector, the isolated native login
 * profiles, the terminal commands that log in / check / start one, and the opt-in statistics of the selected
 * profile. The user's own `claude` command and its default ~/.claude configuration are never touched.
 * Rendered for Claude Code only; that is the only client whose `enabled` is ever true here.
 */
export function ClaudeDirectPanel({ enabled }: { enabled: boolean }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const state = useClaudeDirect(enabled);
  const create = useCreateClaudeProfile();
  const select = useSelectClaudeDirect();
  const [name, setName] = useState('');
  const [copied, setCopied] = useState<string | null>(null);

  const mode = state.data?.mode ?? 'gateway';
  const profiles = state.data?.profiles ?? [];
  const selectedId = state.data?.profileId ?? profiles[0]?.id ?? null;
  const selected = profiles.find((p) => p.id === selectedId);
  const direct = mode === 'direct';
  // Statistics are collected only for a selected direct profile with consent on; polling follows that.
  const live = direct && selected?.telemetryEnabled === true;
  const requests = useClaudeDirectRequests(live ? selected?.id : null, live);
  const busy = create.isPending || select.isPending;

  const copy = async (text: string) => {
    if (!(await systemActions.copy(text))) return;
    setCopied(text);
    toast(t('common.copied'));
  };

  const switchMode = (next: ClaudeDirectMode) => {
    if (next === mode || busy || !state.data) return;
    if (next === 'direct' && !selectedId) {
      toast(t('clients.claudeDirect.needProfileDetail'), 'error');
      return;
    }
    select.mutate({ mode: next, profileId: selectedId }, { onError: (e) => toast(errorText(e), 'error') });
  };

  const chooseProfile = (id: string) => {
    if (busy) return;
    select.mutate({ mode, profileId: id }, { onError: (e) => toast(errorText(e), 'error') });
  };

  const setConsent = (on: boolean) => {
    if (!selected || busy) return;
    select.mutate({ mode, profileId: selected.id, telemetryEnabled: on }, { onError: (e) => toast(errorText(e), 'error') });
  };

  const addProfile = () => {
    const trimmed = name.trim();
    if (!trimmed) return;
    create.mutate(trimmed, {
      onSuccess: () => {
        setName('');
        toast(t('clients.claudeDirect.profileCreated', { name: trimmed }), 'success');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });
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

      {state.data && (
        <Group title={t('clients.claudeDirect.profilesTitle')} footer={t('clients.claudeDirect.profilesFooter')}>
          {profiles.length === 0 && (
            <div className="px-4 py-3 text-[12px] text-[var(--text-secondary)]">{t('clients.claudeDirect.noProfiles')}</div>
          )}
          {profiles.map((p) => (
            <Row
              key={p.id}
              icon={<KeyRound className="size-4 text-[var(--text-secondary)]" />}
              label={
                <span className="flex items-center gap-2">
                  <span className="truncate">{p.name}</span>
                  {p.id === selectedId && <Badge tone="accent">{t('clients.claudeDirect.selectedBadge')}</Badge>}
                  {p.telemetryEnabled && <Badge>{t('clients.claudeDirect.statsBadge')}</Badge>}
                </span>
              }
              detail={p.configDirectory}
              selected={p.id === selectedId}
              onClick={p.id === selectedId ? undefined : () => chooseProfile(p.id)}
            >
              {p.id !== selectedId && <span className="text-[11px] text-[var(--text-secondary)]">{t('clients.claudeDirect.makeSelected')}</span>}
            </Row>
          ))}
          <Row label={t('clients.claudeDirect.addProfile')} detail={t('clients.claudeDirect.addProfileDetail')}>
            <Input
              className="w-44"
              value={name}
              maxLength={80}
              onChange={(e) => setName(e.target.value)}
              placeholder={t('clients.claudeDirect.namePlaceholder')}
              aria-label={t('clients.claudeDirect.name')}
            />
            <Button
              size="sm"
              variant="primary"
              icon={<Plus className="size-3.5" />}
              disabled={busy || !name.trim() || profiles.length >= 20}
              loading={create.isPending}
              onClick={addProfile}
            >
              {t('common.add')}
            </Button>
          </Row>
        </Group>
      )}

      {state.data && !direct && (
        <Alert tone="info" title={t('clients.claudeDirect.gatewayTitle')}>
          {t('clients.claudeDirect.gatewayHint')}
        </Alert>
      )}

      {state.data && !direct && profiles.length > 0 && (
        <Group title={t('clients.claudeDirect.launchTitle')} footer={t('clients.claudeDirect.launchFooter')}>
          <p className="px-4 pt-3 pb-1 text-[11px] text-[var(--text-secondary)]">{t('clients.claudeDirect.launchNote')}</p>
          {selected && (
            <>
              <LauncherRow command={launcherCommand('login', selected.id)} copied={copied} onCopy={copy} />
              <LauncherRow command={launcherCommand('status', selected.id)} copied={copied} onCopy={copy} />
            </>
          )}
        </Group>
      )}

      {state.data && direct && !selected && (
        <Alert tone="warning" title={t('clients.claudeDirect.needProfile')}>
          {t('clients.claudeDirect.needProfileDetail')}
        </Alert>
      )}

      {state.data && direct && selected && (
        <Group title={t('clients.claudeDirect.launchTitle')} footer={t('clients.claudeDirect.launchFooter')}>
          <Alert tone="warning" title={t('clients.claudeDirect.defaultTitle')}>
            {t('clients.claudeDirect.defaultDetail')}
          </Alert>
          <p className="px-4 pt-3 pb-1 text-[11px] text-[var(--text-secondary)]">{t('clients.claudeDirect.launchNote')}</p>
          <LauncherRow command={launcherCommand('login', selected.id)} copied={copied} onCopy={copy} />
          <LauncherRow command={launcherCommand('status', selected.id)} copied={copied} onCopy={copy} />
          <LauncherRow command={launcherCommand('run', selected.id)} copied={copied} onCopy={copy} />
          <p className="px-4 py-2 text-[11px] text-[var(--text-muted)]">{t('clients.claudeDirect.identityNote')}</p>
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

/** One launcher command with its copy button; only the public profile id is copied, never a credential. */
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

/** The selected profile's reported requests: the totals of what is shown, then one row per request. */
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
