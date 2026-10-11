import { ExternalLink, KeyRound, LogIn, LogOut, Plug } from 'lucide-react';
import { useEffect, useState } from 'react';

import {
  pluginPrompt,
  pluginValidate,
  usePluginApiKey,
  usePluginCheckin,
  usePluginCheckinAction,
  usePluginMethods,
  usePluginSignIn,
  usePluginSignOut,
  usePluginUsage,
  useProviderAccounts,
  useStartPluginSignIn,
  useSubmitPluginCode,
  useSubscriptionPolicy,
  useUpdateSubscriptionPolicy,
} from '../api/hooks';
import type { PluginAuthMethod, PluginPrompt, Provider, ProviderAccount } from '../api/types';
import { Alert } from './arc/alert/alert';
import { SwitchModePicker } from './SubscriptionAccounts';
import { Badge, Button, EmptyState, Field, Input, KV, Spinner, Switch } from './ui/controls';
import { errorText, Select, Sheet, useFeedback } from './ui/overlays';
import { useI18n, type MessageKey } from '../i18n';
import { formatDateTime } from '../lib/format';

/**
 * Accounts of a plugin's provider (auth scheme "plugin"): the sign-ins its plugin keeps. Signing in runs the
 * plugin's own methods — a key, or a browser page the plugin finishes on its own or with a pasted code — after
 * whatever questions the method asks. Each account's allowance is the plugin's word on it.
 */
export function PluginAccounts({ provider }: { provider: Provider }) {
  const { t } = useI18n();
  const accounts = useProviderAccounts(provider.id);
  const methods = usePluginMethods(provider.id);
  // with more than one sign-in: how requests pick between them (the same switch modes as a subscription's)
  const policy = useSubscriptionPolicy(provider.id, true);
  const updatePolicy = useUpdateSubscriptionPolicy(provider.id);
  const { toast } = useFeedback();
  const [method, setMethod] = useState<PluginAuthMethod | null>(null);

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-2">
        {methods.isLoading && <Spinner lines={1} />}
        {methods.isError && <Alert tone="danger" title={t('pluginAccounts.noPlugin')}>{errorText(methods.error)}</Alert>}
        {methods.data?.map((m) => (
          <Button
            key={m.index}
            variant={m.index === 0 ? 'primary' : undefined}
            icon={m.type === 'api' ? <KeyRound className="size-3.5" /> : <LogIn className="size-3.5" />}
            onClick={() => setMethod(m)}
          >
            {m.label || t(m.type === 'api' ? 'pluginAccounts.apiKey' : 'pluginAccounts.browser')}
          </Button>
        ))}
      </div>
      {accounts.isLoading && <Spinner lines={3} />}
      {accounts.data?.length === 0 && (
        <div className="card">
          <EmptyState icon={<Plug className="size-6" />} title={t('pluginAccounts.empty')} detail={t('pluginAccounts.emptyDetail')} />
        </div>
      )}
      {accounts.data && accounts.data.length > 1 && policy.data && (
        <div className="flex flex-wrap items-center gap-3 px-1">
          <p className="min-w-0 flex-1 text-[12px] text-[var(--text-secondary)]">
            {t(
              policy.data.switchMode === 'balanced'
                ? 'providers.subscription.policy.balancedDetail'
                : policy.data.switchMode === 'failover'
                  ? 'providers.subscription.policy.failoverDetail'
                  : 'providers.subscription.policy.manualDetail',
            )}
          </p>
          <SwitchModePicker
            policy={policy.data}
            onChange={(switchMode) => updatePolicy.mutate({ switchMode }, { onError: (e) => toast(errorText(e), 'error') })}
          />
        </div>
      )}
      {accounts.data && accounts.data.length > 0 && (
        <div className="card divide-y divide-[var(--border-subtle)] overflow-hidden p-0">
          {accounts.data.map((a) => (
            <AccountRow key={a.id} provider={provider} account={a} />
          ))}
        </div>
      )}
      <CheckinSection provider={provider} />
      {method && <SignInSheet provider={provider} method={method} onClose={() => setMethod(null)} />}
    </div>
  );
}

/** The plugin's daily check-in (签到), when its plugin presses one: off until switched on. */
function CheckinSection({ provider }: { provider: Provider }) {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const state = usePluginCheckin(provider.id);
  const action = usePluginCheckinAction(provider.id);
  if (!state.data?.supported) return null;
  const run = (on?: boolean) => action.mutate(on, { onError: (e) => toast(errorText(e), 'error') });
  return (
    <section className="card flex flex-col gap-2">
      <div className="flex flex-wrap items-center gap-3">
        <span className="flex-1 text-[13px] font-medium">{t('pluginAccounts.checkin')}</span>
        <Switch checked={state.data.on} label={t('pluginAccounts.checkinOn')} onChange={(on) => run(on)} />
        <Button size="sm" disabled={action.isPending} onClick={() => run()}>
          {t('pluginAccounts.checkinNow')}
        </Button>
      </div>
      <p className="text-[12px] text-[var(--text-secondary)]">{t('pluginAccounts.checkinDetail')}</p>
      {state.data.accounts.map((a) => (
        <KV key={a.accountId} label={a.account}>
          {a.outcome
            ? [
                t(`pluginAccounts.outcome.${a.outcome}` as MessageKey),
                a.credit ? t('pluginAccounts.credit', { credit: a.credit }) : null,
                a.streak ? t('pluginAccounts.streak', { days: a.streak }) : null,
                a.message,
                a.at ? formatDateTime(a.at, locale) : null,
              ]
                .filter(Boolean)
                .join(' · ')
            : t('pluginAccounts.checkinNever')}
        </KV>
      ))}
    </section>
  );
}

function AccountRow({ provider, account: a }: { provider: Provider; account: ProviderAccount }) {
  const { t, locale } = useI18n();
  const { toast, confirm } = useFeedback();
  const signOut = usePluginSignOut(provider.id);
  const [showUsage, setShowUsage] = useState(false);
  const usageTold = (provider.settings?.plugin as { usage?: boolean } | undefined)?.usage === true;
  const usage = usePluginUsage(provider.id, a.id, showUsage);

  const doSignOut = async () => {
    if (!(await confirm({ title: t('pluginAccounts.signOutConfirm', { name: a.displayName }), confirmLabel: t('pluginAccounts.signOut'), destructive: true }))) return;
    signOut.mutate(a.id, { onError: (e) => toast(errorText(e), 'error') });
  };

  return (
    <article className="flex flex-col gap-2 px-4 py-3">
      <div className="flex flex-wrap items-center gap-2">
        <span className="min-w-0 flex-1 truncate text-[13px] font-medium">{a.displayName}</span>
        {a.isCurrent && <Badge tone="accent">{t('pluginAccounts.current')}</Badge>}
        <Badge tone={a.status === 'active' ? 'green' : 'red'}>{t(a.status === 'active' ? 'pluginAccounts.active' : 'pluginAccounts.lapsed')}</Badge>
        {usageTold && (
          <Button size="sm" onClick={() => setShowUsage((s) => !s)}>
            {t('pluginAccounts.usage')}
          </Button>
        )}
        <Button size="sm" variant="destructive" icon={<LogOut className="size-3.5" />} onClick={() => void doSignOut()}>
          {t('pluginAccounts.signOut')}
        </Button>
      </div>
      {a.status !== 'active' && <p className="text-[12px] text-[var(--danger)]">{t('pluginAccounts.lapsedDetail')}</p>}
      {showUsage && (
        <div className="flex flex-col gap-1 rounded-md bg-[var(--surface-muted)] px-3 py-2">
          {usage.isLoading && <Spinner lines={2} />}
          {usage.isError && <span className="text-[12px] text-[var(--danger)]">{errorText(usage.error)}</span>}
          {usage.data?.error && <span className="text-[12px] text-[var(--danger)]">{usage.data.error}</span>}
          {usage.data?.plan && <KV label={t('pluginAccounts.plan')}>{usage.data.plan}</KV>}
          {usage.data?.until && <KV label={t('pluginAccounts.until')}>{formatDateTime(usage.data.until, locale)}</KV>}
          {usage.data?.windows?.map((w) => (
            <KV key={w.name} label={w.name}>
              {w.display ?? (w.limit ? `${w.amount ?? w.used ?? 0} / ${w.limit} ${w.unit ?? ''}` : `${w.used ?? 0}%`)}
              {w.resetsAt ? ` · ${t('pluginAccounts.resets', { at: formatDateTime(new Date(w.resetsAt > 1e12 ? w.resetsAt : w.resetsAt * 1000).toISOString(), locale) })}` : ''}
            </KV>
          ))}
        </div>
      )}
    </article>
  );
}

/**
 * One sign-in with a plugin's method: its questions one by one (the plugin says which comes next, and checks each
 * answer), then a key, or a browser page — whose code is pasted back, or which the plugin notices on its own.
 */
function SignInSheet({ provider, method, onClose }: { provider: Provider; method: PluginAuthMethod; onClose: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const [inputs, setInputs] = useState<Record<string, string>>({});
  const [prompt, setPrompt] = useState<PluginPrompt | null | undefined>(undefined);
  const [answer, setAnswer] = useState('');
  const [answerError, setAnswerError] = useState<string | null>(null);
  const [key, setKey] = useState('');
  const [code, setCode] = useState('');
  const [flow, setFlow] = useState<string | null>(null);
  const start = useStartPluginSignIn(provider.id);
  const apiKey = usePluginApiKey(provider.id);
  const submitCode = useSubmitPluginCode(provider.id);
  const signIn = usePluginSignIn(provider.id, flow);
  const s = signIn.data;

  // the method's next question, given the answers so far (none left: on to the key or the page)
  useEffect(() => {
    let live = true;
    pluginPrompt(provider.id, method.index, inputs)
      .then((r) => {
        if (!live) return;
        setPrompt(r.prompt);
        setAnswer(r.prompt?.type === 'select' ? (r.prompt.options?.[0]?.value ?? '') : '');
      })
      .catch((e) => live && toast(errorText(e), 'error'));
    return () => {
      live = false;
    };
  }, [provider.id, method.index, inputs, toast]);

  useEffect(() => {
    if (s?.state === 'done') {
      toast(t('pluginAccounts.signedIn'), 'success');
      onClose();
    }
  }, [s?.state, onClose, t, toast]);

  const answerPrompt = async () => {
    if (!prompt) return;
    const check = await pluginValidate(provider.id, method.index, prompt.key, answer).catch(() => ({ error: null }));
    if (check.error) return setAnswerError(check.error);
    setAnswerError(null);
    setInputs((i) => ({ ...i, [prompt.key]: answer }));
  };

  const begin = () =>
    start.mutate(
      { method: method.index, inputs },
      {
        onSuccess: (r) => {
          setFlow(r.id);
          if (r.url) window.open(r.url, '_blank', 'noopener');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  const useKey = () =>
    apiKey.mutate(
      { method: method.index, inputs, key },
      { onSuccess: () => (toast(t('pluginAccounts.signedIn'), 'success'), onClose()), onError: (e) => toast(errorText(e), 'error') },
    );

  return (
    <Sheet open onOpenChange={(o) => !o && onClose()} title={t('pluginAccounts.signInTitle', { name: provider.name })} description={method.label} width={520}>
      {prompt === undefined && <Spinner lines={2} />}
      {prompt && (
        <div className="flex flex-col gap-3">
          <Field label={prompt.message} error={answerError ?? undefined}>
            {prompt.type === 'select' ? (
              <Select
                value={answer}
                onChange={setAnswer}
                options={(prompt.options ?? []).map((o) => ({ value: o.value, label: o.label, description: o.hint ?? undefined }))}
              />
            ) : (
              <Input value={answer} placeholder={prompt.placeholder ?? undefined} onChange={(e) => setAnswer(e.target.value)} autoFocus />
            )}
          </Field>
          <div className="flex justify-end">
            <Button variant="primary" onClick={() => void answerPrompt()}>
              {t('pluginAccounts.continue')}
            </Button>
          </div>
        </div>
      )}
      {prompt === null && method.type === 'api' && (
        <div className="flex flex-col gap-3">
          <Field label={t('pluginAccounts.key')}>
            <Input mono type="password" value={key} placeholder={method.placeholder ?? undefined} onChange={(e) => setKey(e.target.value)} autoFocus />
          </Field>
          <div className="flex justify-end">
            <Button variant="primary" disabled={!key.trim() || apiKey.isPending} onClick={useKey}>
              {t('pluginAccounts.signIn')}
            </Button>
          </div>
        </div>
      )}
      {prompt === null && method.type !== 'api' && !s && (
        <div className="flex justify-end">
          <Button variant="primary" icon={<ExternalLink className="size-3.5" />} disabled={start.isPending} onClick={begin}>
            {t('pluginAccounts.openPage')}
          </Button>
        </div>
      )}
      {s && s.state !== 'done' && (
        <div className="flex flex-col gap-3">
          {s.instructions && <p className="whitespace-pre-wrap text-[12px] text-[var(--text-secondary)]">{s.instructions}</p>}
          {s.deviceCode && (
            <KV label={t('pluginAccounts.deviceCode')} mono>
              {s.deviceCode}
            </KV>
          )}
          {s.url && (
            <Button icon={<ExternalLink className="size-3.5" />} onClick={() => window.open(s.url!, '_blank', 'noopener')}>
              {t('pluginAccounts.openAgain')}
            </Button>
          )}
          {s.state === 'waiting' && s.pasteCode && (
            <>
              <Field label={t('pluginAccounts.code')}>
                <Input mono value={code} onChange={(e) => setCode(e.target.value)} autoFocus />
              </Field>
              <div className="flex justify-end">
                <Button
                  variant="primary"
                  disabled={!code.trim() || submitCode.isPending}
                  onClick={() => submitCode.mutate({ flow: s.id, code: code.trim() }, { onError: (e) => toast(errorText(e), 'error') })}
                >
                  {t('pluginAccounts.signIn')}
                </Button>
              </div>
            </>
          )}
          {s.state === 'waiting' && !s.pasteCode && <p className="text-[12px] text-[var(--text-muted)]">{t('pluginAccounts.waiting')}</p>}
          {s.state === 'failed' && <Alert tone="danger" title={t('pluginAccounts.failed')}>{s.error}</Alert>}
        </div>
      )}
    </Sheet>
  );
}
