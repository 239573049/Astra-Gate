import { ExternalLink, LogIn, ShieldCheck } from 'lucide-react';
import { useEffect, useState } from 'react';

import {
  useAddClaudeBridgeAccount,
  useClaudeBridge,
  useClaudeLogin,
  useClaudeSignIn,
  useStartClaudeLogin,
  useUpdateClaudeBridgeSettings,
} from '../api/hooks';
import type { Provider, ProviderAccount } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { Alert } from './arc/alert/alert';
import { Badge, Button, Field, Group, Input, NumberInput, Row } from './ui/controls';
import { errorText, useFeedback } from './ui/overlays';

const SOURCE_KEY: Record<string, MessageKey> = {
  settings: 'claudeBridge.source.settings',
  path: 'claudeBridge.source.path',
  installer: 'claudeBridge.source.installer',
};

/**
 * The Claude subscription's "through the claude program" mode (linked update 36, magpie's Claude subscription bridge): what
 * `claude` was found, its run settings, the accounts that are answered through it (kind `claude_bridge`, opt-in: nothing changes
 * for the OAuth accounts) with each one's sign-in, and the form that adds one. Shown on the Claude subscription provider only.
 */
export function ClaudeBridgePanel({ provider, accounts }: { provider: Provider; accounts: ProviderAccount[] }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const status = useClaudeBridge();
  const update = useUpdateClaudeBridgeSettings();
  const add = useAddClaudeBridgeAccount();
  const bridgeAccounts = accounts.filter((a) => a.kind === 'claude_bridge');
  const [binary, setBinary] = useState('');
  const [maxRuns, setMaxRuns] = useState<number | null>(6);
  const [idle, setIdle] = useState<number | null>(10);
  const [name, setName] = useState('');
  const [dir, setDir] = useState('');
  const [proxy, setProxy] = useState('');

  const s = status.data;
  useEffect(() => {
    if (!s) return;
    setBinary(s.settings.binaryPath ?? '');
    setMaxRuns(s.settings.maxRuns);
    setIdle(s.settings.idleMinutes);
  }, [s?.settings.binaryPath, s?.settings.maxRuns, s?.settings.idleMinutes]); // eslint-disable-line react-hooks/exhaustive-deps

  if (!s) return null;
  const saveSettings = () =>
    update.mutate(
      { binaryPath: binary.trim(), maxRuns: maxRuns ?? 6, idleMinutes: idle ?? 10 },
      { onSuccess: () => toast(t('claudeBridge.saved'), 'success'), onError: (e) => toast(errorText(e), 'error') },
    );
  const addAccount = () =>
    add.mutate(
      { providerId: provider.id, displayName: name.trim() || undefined, configDir: dir.trim(), httpProxy: proxy.trim() || undefined },
      {
        onSuccess: () => {
          toast(t('claudeBridge.added'), 'success');
          setName('');
          setDir('');
          setProxy('');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  return (
    <Group title={t('claudeBridge.title')} footer={t('claudeBridge.hint')}>
      <Row
        label={s.found ? (s.version ? t('claudeBridge.found', { version: s.version }) : t('claudeBridge.foundNoVersion')) : t('claudeBridge.notFound')}
        detail={s.found && s.path ? t('claudeBridge.path', { path: s.path, source: s.source ? t(SOURCE_KEY[s.source] ?? 'claudeBridge.source.path') : '' }) : undefined}
      >
        <Badge tone={s.found ? 'green' : 'orange'}>{s.found ? 'claude' : '—'}</Badge>
        {s.runs.length > 0 && <Badge tone="neutral">{t('claudeBridge.runs', { n: s.runs.length })}</Badge>}
      </Row>
      {s.runs.length > 0 && (
        <div className="px-4 py-2 text-[11.5px] text-[var(--text-secondary)]">
          {s.runs.map((r) => (
            <div key={r.id} className="flex flex-wrap gap-x-3">
              <span className="font-mono">{r.pid ?? '—'}</span>
              <span>{r.model}</span>
              <span>{t(`claudeBridge.run.${r.state}` as MessageKey)}</span>
              <span className="text-[var(--text-muted)]">{r.configDir || t('claudeBridge.defaultDir')}</span>
            </div>
          ))}
        </div>
      )}
      <div className="grid gap-3 px-4 py-3 sm:grid-cols-3">
        <Field label={t('claudeBridge.binaryPath')}>
          <Input mono value={binary} placeholder={t('claudeBridge.binaryPathPlaceholder')} onChange={(e) => setBinary(e.target.value)} />
        </Field>
        <Field label={t('claudeBridge.maxRuns')}>
          <NumberInput value={maxRuns} min={1} onChange={setMaxRuns} />
        </Field>
        <Field label={t('claudeBridge.idleMinutes')}>
          <NumberInput value={idle} min={1} onChange={setIdle} />
        </Field>
        <div className="sm:col-span-3">
          <Button size="sm" disabled={update.isPending} onClick={saveSettings}>
            {t('common.save')}
          </Button>
        </div>
      </div>

      {s.clientPolicy === 'claude-code-only' && (
        <div className="px-4 py-2">
          <Alert tone="info" title={t('claudeBridge.clientPolicy')} />
        </div>
      )}
      <div className="px-4 py-2 text-[11px] text-[var(--text-muted)]">{t('claudeBridge.cost')}</div>

      {bridgeAccounts.map((a) => (
        <BridgeAccountRow key={a.id} account={a} found={s.found} />
      ))}

      <div className="grid gap-3 px-4 py-3 sm:grid-cols-3">
        <Field label={t('claudeBridge.add.name')}>
          <Input value={name} onChange={(e) => setName(e.target.value)} />
        </Field>
        <Field label={t('claudeBridge.add.configDir')} hint={t('claudeBridge.add.configDirHint')}>
          <Input mono value={dir} placeholder="~/.claude-work" onChange={(e) => setDir(e.target.value)} />
        </Field>
        <Field label={t('claudeBridge.add.proxy')}>
          <Input mono value={proxy} placeholder="http://127.0.0.1:7890" onChange={(e) => setProxy(e.target.value)} />
        </Field>
        <div className="sm:col-span-3">
          <Button size="sm" variant="primary" disabled={add.isPending} onClick={addAccount}>
            {t('claudeBridge.add')}
          </Button>
        </div>
      </div>
    </Group>
  );
}

/** One bridge account: its directory, who `claude auth status` says is signed in there, and a sign-in button. */
function BridgeAccountRow({ account, found }: { account: ProviderAccount; found: boolean }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const [check, setCheck] = useState(false);
  const signIn = useClaudeSignIn(check ? account.id : null);
  const start = useStartClaudeLogin();
  const [logging, setLogging] = useState(false);
  const login = useClaudeLogin(logging ? account.id : null);
  const state = login.data?.state;

  useEffect(() => {
    if (state === 'done') {
      setLogging(false);
      setCheck(true);
      void signIn.refetch();
      toast(t('claudeBridge.login.done'), 'success');
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [state]);

  const who = signIn.data?.loggedIn ? [signIn.data.email, signIn.data.plan].filter(Boolean).join(' · ') : '';
  const detail = signIn.data
    ? !signIn.data.known
      ? t('claudeBridge.signin.unknown', { error: signIn.data.error ?? '' })
      : signIn.data.loggedIn
        ? t('claudeBridge.signin.ok', { who })
        : t('claudeBridge.signin.out')
    : t('claudeBridge.dir', { dir: account.claudeConfigDir || t('claudeBridge.defaultDir') });

  return (
    <Row label={account.displayName || account.id} detail={detail}>
      <Button size="sm" variant="plain" icon={<ShieldCheck className="size-3.5" />} disabled={!found} onClick={() => (check ? void signIn.refetch() : setCheck(true))}>
        {t('claudeBridge.signin.check')}
      </Button>
      <Button
        size="sm"
        icon={<LogIn className="size-3.5" />}
        disabled={!found || start.isPending || state === 'waiting'}
        onClick={() => start.mutate(account.id, { onSuccess: () => setLogging(true), onError: (e) => toast(errorText(e), 'error') })}
      >
        {t('claudeBridge.login')}
      </Button>
      {logging && state === 'waiting' && (
        <span className="flex items-center gap-2 text-[11.5px] text-[var(--text-secondary)]">
          {t('claudeBridge.login.waiting')}
          {login.data?.url && (
            <a className="inline-flex items-center gap-1 text-[var(--accent)]" href={login.data.url} target="_blank" rel="noopener noreferrer">
              {t('claudeBridge.login.open')} <ExternalLink className="size-3" />
            </a>
          )}
        </span>
      )}
      {logging && state === 'failed' && <span className="text-[11.5px] text-[var(--danger)]">{t('claudeBridge.login.failed', { message: login.data?.message ?? '' })}</span>}
    </Row>
  );
}
