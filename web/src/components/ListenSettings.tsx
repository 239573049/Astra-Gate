import { Copy } from 'lucide-react';
import { useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';

import { useAuthStatus, useListen, useSetAdminPassword, useUpdateListen } from '../api/hooks';
import { useI18n } from '../i18n';
import { isDesktop } from '../shell/bridge';
import { systemActions } from '../shell/systemActions';
import { PasswordRow } from './RemoteAccessSettings';
import { Button, Input, Row, Segmented, Spinner } from './ui/controls';
import { errorText, Sheet, useFeedback } from './ui/overlays';

type Scope = 'local' | 'lan';

const LOOPBACK_HOSTS = ['127.0.0.1', 'localhost', '::1', '[::1]'];
const HOSTS: Record<Scope, string> = { local: '127.0.0.1', lan: '0.0.0.0' };
const scopeOf = (host: string): Scope => (LOOPBACK_HOSTS.includes(host) ? 'local' : 'lan');

/**
 * Settings › Gateway: listen on this machine only (127.0.0.1) or on the LAN (0.0.0.0). The address goes to config.json
 * and applies at the next start, so a pending change offers a restart (desktop) or `astra restart`. LAN access needs an
 * admin password — without one, choosing it asks for the password first — and only this machine may change it.
 */
export function ListenRows() {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const qc = useQueryClient();
  const listen = useListen();
  const auth = useAuthStatus();
  const update = useUpdateListen();
  const [askPassword, setAskPassword] = useState(false);
  const [restarting, setRestarting] = useState(false);
  const s = listen.data;

  if (!s) {
    return (
      <Row label={t('settings.listen')} detail={listen.isError ? errorText(listen.error) : undefined}>
        {listen.isPending && <Spinner className="size-3.5" />}
      </Row>
    );
  }

  const address = `${s.host}:${s.port}`;
  const scope = scopeOf(s.configuredHost);
  const pending = s.configuredHost !== s.host;
  const fromOtherDevice = !!auth.data?.required;

  const copy = async (text: string) => {
    if (await systemActions.copy(text)) toast(t('common.copied'));
  };

  const save = (next: Scope) =>
    update.mutate(HOSTS[next], {
      onSuccess: (r) => r.configuredHost !== r.host && toast(t('settings.listen.saved')),
      onError: (e) => toast(errorText(e), 'error'),
    });

  const choose = async (next: Scope) => {
    if (next === scope) return;
    if (next === 'lan') {
      if (!s.passwordSet) {
        setAskPassword(true);
        return;
      }
      if (!(await confirm({ title: t('settings.listen.lanConfirm'), detail: t('settings.listen.lanDetail'), confirmLabel: t('settings.listen.lanAction') })))
        return;
    }
    save(next);
  };

  const restart = async () => {
    setRestarting(true);
    const r = await systemActions.restartService();
    setRestarting(false);
    if (!r) return;
    if (!r.ok) {
      toast(r.error ?? t('common.error'), 'error');
      return;
    }
    toast(t('settings.listen.restarted'));
    void qc.invalidateQueries();
  };

  const detail = s.hostFromEnvironment
    ? t('settings.listen.env', { address })
    : fromOtherDevice
      ? t('settings.listen.localOnly', { address })
      : pending
        ? `${t('settings.listen.pending', { host: s.configuredHost })}${isDesktop ? '' : ` · ${t('settings.listen.restartHint')}`}`
        : address;

  return (
    <>
      <Row label={t('settings.listen')} detail={detail}>
        {!s.hostFromEnvironment && !fromOtherDevice && (
          <>
            {pending && isDesktop && (
              <Button size="sm" loading={restarting} onClick={() => void restart()}>
                {t('settings.listen.restart')}
              </Button>
            )}
            <Segmented
              ariaLabel={t('settings.listen')}
              value={scope}
              onChange={(v) => void choose(v)}
              items={[
                { value: 'local', label: t('settings.listen.local') },
                { value: 'lan', label: t('settings.listen.lan') },
              ]}
            />
          </>
        )}
      </Row>
      {s.networkUrls.map((url) => (
        <Row key={url} label={t('settings.listen.networkUrl')} detail={`${url}/v1`}>
          <Button size="sm" variant="plain" icon={<Copy className="size-3.5" />} aria-label={t('common.copy')} onClick={() => void copy(`${url}/v1`)} />
        </Row>
      ))}
      {(scope === 'lan' || s.networkUrls.length > 0) && <PasswordRow passwordSet={s.passwordSet} />}
      <PasswordSheet open={askPassword} onOpenChange={setAskPassword} onSaved={() => save('lan')} />
    </>
  );
}

/** Asked before LAN access when no admin password exists yet: sets it (twice, at least 8 characters), then switches. */
function PasswordSheet({ open, onOpenChange, onSaved }: { open: boolean; onOpenChange: (open: boolean) => void; onSaved: () => void }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const setPassword = useSetAdminPassword();
  const [password, setPasswordText] = useState('');
  const [repeat, setRepeat] = useState('');
  const mismatch = repeat !== '' && repeat !== password;
  const valid = password.length >= 8 && password === repeat;

  const close = (o: boolean) => {
    if (!o) {
      setPasswordText('');
      setRepeat('');
    }
    onOpenChange(o);
  };

  const submit = () => {
    if (!valid || setPassword.isPending) return;
    setPassword.mutate(password, {
      onSuccess: () => {
        toast(t('settings.remote.passwordSaved'));
        close(false);
        onSaved();
      },
      onError: (e) => toast(errorText(e), 'error'),
    });
  };

  return (
    <Sheet
      open={open}
      onOpenChange={close}
      title={t('settings.listen.passwordTitle')}
      description={t('settings.listen.passwordDetail')}
      width={420}
      footer={
        <>
          <Button onClick={() => close(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!valid} loading={setPassword.isPending} onClick={submit}>
            {t('settings.listen.passwordAction')}
          </Button>
        </>
      }
    >
      <form
        className="flex flex-col gap-3"
        onSubmit={(e) => {
          e.preventDefault();
          submit();
        }}
      >
        <Input
          type="password"
          autoComplete="new-password"
          autoFocus
          aria-label={t('settings.remote.passwordNew')}
          placeholder={t('settings.remote.passwordNew')}
          value={password}
          onChange={(e) => setPasswordText(e.target.value)}
        />
        <Input
          type="password"
          autoComplete="new-password"
          aria-label={t('settings.listen.passwordRepeat')}
          placeholder={t('settings.listen.passwordRepeat')}
          invalid={mismatch}
          value={repeat}
          onChange={(e) => setRepeat(e.target.value)}
        />
        {mismatch && <div className="text-[12px] text-[var(--danger)]">{t('settings.listen.passwordMismatch')}</div>}
        <div className="text-[12px] text-[var(--text-secondary)]">{t('settings.listen.lanDetail')}</div>
        <button type="submit" hidden />
      </form>
    </Sheet>
  );
}
