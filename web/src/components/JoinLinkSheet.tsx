// One-time join links of a token: the admin makes a short-lived link, sends it to the employee, and the employee's Astra
// exchanges it once for the key. The link holds the gateway address and a one-time code, never the key.

import { Link2 } from 'lucide-react';
import { useState } from 'react';

import { useCreateTokenInvite, useRevokeTokenInvite, useTokenInvites } from '../api/hooks';
import type { Token, TokenInvite } from '../api/types';
import { Badge, Button, Segmented } from './ui/controls';
import { errorText, Sheet, useFeedback } from './ui/overlays';
import { useI18n } from '../i18n';
import { formatDateTime } from '../lib/format';
import { systemActions } from '../shell/systemActions';

const LIFETIMES = [15, 60, 1440] as const;

export function JoinLinkSheet({ token, open, onOpenChange }: { token: Token; open: boolean; onOpenChange: (open: boolean) => void }) {
  const { t, locale } = useI18n();
  const { toast } = useFeedback();
  const [minutes, setMinutes] = useState<(typeof LIFETIMES)[number]>(15);
  const [link, setLink] = useState<string | null>(null);
  const invites = useTokenInvites(token.id, open);
  const create = useCreateTokenInvite(token.id);
  const revoke = useRevokeTokenInvite(token.id);

  const lifetimeLabel = (m: number) => (m < 60 ? t('tokens.invite.minutes', { n: m }) : t('tokens.invite.hours', { n: m / 60 }));

  const generate = () =>
    create.mutate(minutes, {
      onSuccess: async (made) => {
        setLink(made.link);
        toast((await systemActions.copy(made.link)) ? t('tokens.invite.copied') : t('tokens.copyFailed'), 'success');
      },
      onError: (e) => toast(errorText(e), 'error'),
    });

  const statusTone = (s: TokenInvite['status']) => (s === 'pending' ? 'accent' : s === 'used' ? 'green' : 'neutral');

  return (
    <Sheet
      open={open}
      onOpenChange={(next) => {
        if (!next) setLink(null);
        onOpenChange(next);
      }}
      title={t('tokens.invite.title', { name: token.name })}
      description={t('tokens.invite.description')}
      footer={
        <Button variant="primary" icon={<Link2 className="size-3.5" />} loading={create.isPending} onClick={generate}>
          {t('tokens.invite.generate')}
        </Button>
      }
    >
      <div className="grid gap-4">
        <div className="grid gap-1.5">
          <span className="text-[12px] text-[var(--text-secondary)]">{t('tokens.invite.lifetime')}</span>
          <Segmented
            items={LIFETIMES.map((m) => ({ value: String(m), label: lifetimeLabel(m) }))}
            value={String(minutes)}
            onChange={(value) => setMinutes(Number(value) as (typeof LIFETIMES)[number])}
            ariaLabel={t('tokens.invite.lifetime')}
          />
        </div>
        {link && (
          <div className="grid gap-1.5">
            <span className="text-[12px] text-[var(--text-secondary)]">{t('tokens.invite.linkOnce')}</span>
            <code className="selectable break-all rounded-lg bg-[var(--surface-muted)] px-3 py-2 font-mono text-[11.5px]">{link}</code>
          </div>
        )}
        <div className="grid gap-1.5">
          <span className="text-[12px] text-[var(--text-secondary)]">{t('tokens.invite.recent')}</span>
          {(invites.data ?? []).length === 0 ? (
            <p className="text-[12px] text-[var(--text-muted)]">{t('tokens.invite.none')}</p>
          ) : (
            <ul className="grid gap-1.5">
              {(invites.data ?? []).map((invite) => (
                <li key={invite.id} className="flex flex-wrap items-center gap-2 rounded-lg border border-[var(--border)] px-3 py-2 text-[12px]">
                  <Badge tone={statusTone(invite.status)}>{t(`tokens.invite.status.${invite.status}`)}</Badge>
                  <span className="text-[var(--text-secondary)]">
                    {invite.status === 'used'
                      ? t('tokens.invite.usedAt', { time: formatDateTime(invite.usedAt, locale), ip: invite.usedIp ?? '—' })
                      : t('tokens.invite.expires', { time: formatDateTime(invite.expiresAt, locale) })}
                  </span>
                  {invite.status === 'pending' && (
                    <Button
                      size="sm"
                      variant="plain"
                      className="ml-auto"
                      loading={revoke.isPending && revoke.variables === invite.id}
                      onClick={() => revoke.mutate(invite.id, { onError: (e) => toast(errorText(e), 'error') })}
                    >
                      {t('tokens.invite.revoke')}
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          )}
        </div>
      </div>
    </Sheet>
  );
}
