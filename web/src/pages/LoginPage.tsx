import { useState, type FormEvent } from 'react';

import { useLogin } from '../api/hooks';
import { Alert } from '../components/arc/alert/alert';
import { PasswordField } from '../components/arc/password-field/password-field';
import { AstraLogo } from '../components/AstraLogo';
import { Button } from '../components/ui/controls';
import { errorText } from '../components/ui/overlays';
import { useT } from '../i18n';

export function LoginPage() {
  const t = useT();
  const login = useLogin();
  const [password, setPassword] = useState('');

  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (password) login.mutate(password);
  };

  return (
    <div className="wallpaper flex h-full items-center justify-center p-4">
      <form
        onSubmit={submit}
        className="flex w-[360px] flex-col gap-5 rounded-[var(--radius-surface)] border border-[var(--border)] bg-[var(--surface-raised)] p-8 shadow-[var(--shadow-floating)]"
      >
        <div className="flex flex-col items-center gap-2 text-center">
          <AstraLogo className="size-12 rounded-[14px]" />
          <h1 className="text-[17px] font-medium">Astra</h1>
          <p className="text-[13px] text-[var(--text-secondary)]">{t('login.subtitle')}</p>
        </div>
        <PasswordField
          label={t('login.password')}
          autoFocus
          autoComplete="current-password"
          value={password}
          aria-invalid={login.isError || undefined}
          onChange={(e) => setPassword(e.target.value)}
        />
        {login.isError && <Alert tone="danger" title={errorText(login.error)} />}
        <Button type="submit" variant="primary" className="w-full" loading={login.isPending} disabled={!password}>
          {t('login.submit')}
        </Button>
      </form>
    </div>
  );
}
