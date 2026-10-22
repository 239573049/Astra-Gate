// Moving an API-key provider onto another template (POST /api/providers/{id}/template-switch), and the hint that offers it
// when the upstream refuses the key and the template names key alternatives (issue #9: RoutinAI's plan endpoint answers a
// pay-as-you-go key with 401).
import { useEffect, useState } from 'react';

import { useSwitchTemplate, useTemplates } from '../api/hooks';
import type { Provider, ProviderTemplate } from '../api/types';
import { useI18n } from '../i18n';
import { PROTOCOL_OPTIONS } from '../pages/ProvidersPage';
import { Alert } from './arc/alert/alert';
import { Button, Field, Spinner } from './ui/controls';
import { errorText, Select, Sheet, useFeedback } from './ui/overlays';

/** A provider signed in with accounts (subscription logins, plugin sign-ins) has no template to switch. */
export const canSwitchTemplate = (p: Provider) => p.authScheme !== 'oauth-subscription' && p.authScheme !== 'plugin';

/** Templates a provider can move onto: the services that take an API key (what the server accepts). */
const switchable = (x: ProviderTemplate) => x.category !== 'subscription' && x.category !== 'custom' && x.authScheme !== 'oauth-subscription';

export function TemplateSwitchSheet({ open, onOpenChange, provider }: { open: boolean; onOpenChange: (o: boolean) => void; provider: Provider }) {
  const { t } = useI18n();
  const { toast } = useFeedback();
  const templates = useTemplates();
  const switchTemplate = useSwitchTemplate(provider.id);
  const [templateId, setTemplateId] = useState('');
  const [variantId, setVariantId] = useState('');
  const choices = (templates.data ?? []).filter(switchable);

  useEffect(() => {
    if (open) {
      setTemplateId(provider.templateId ?? '');
      setVariantId('');
    }
  }, [open, provider.templateId]);

  const target = choices.find((x) => x.id === templateId);
  const endpoints = target ? (target.variants?.find((v) => v.id === variantId)?.endpointsOverride ?? target.endpoints) : [];
  const unchanged = templateId === provider.templateId && !variantId;
  const apply = () =>
    target &&
    switchTemplate.mutate(
      { templateId: target.id, variantId: variantId || null },
      {
        onSuccess: () => {
          onOpenChange(false);
          toast(t('providers.templateSwitched', { name: target.name }), 'success');
        },
        onError: (e) => toast(errorText(e), 'error'),
      },
    );

  return (
    <Sheet
      open={open}
      onOpenChange={onOpenChange}
      title={t('providers.templateSwitch')}
      description={t('providers.templateSwitchDetail')}
      footer={
        <>
          <Button onClick={() => onOpenChange(false)}>{t('common.cancel')}</Button>
          <Button variant="primary" disabled={!target || unchanged} loading={switchTemplate.isPending} onClick={apply}>
            {t('common.apply')}
          </Button>
        </>
      }
    >
      {templates.isLoading && <Spinner />}
      {templates.isError && <Alert tone="danger" title={t('common.error')}>{errorText(templates.error)}</Alert>}
      <div className="grid gap-4">
        <Field label={t('providers.templateSwitchTarget')}>
          <Select
            value={templateId}
            onChange={(v) => {
              setTemplateId(v);
              setVariantId('');
            }}
            options={choices.map((x) => ({
              value: x.id,
              label: x.id === provider.templateId ? t('providers.templateSwitchCurrent', { name: x.name }) : x.name,
              detail: x.id,
            }))}
          />
        </Field>
        {target?.variants && target.variants.length > 0 && (
          <Field label={t('providers.variant')}>
            <Select
              value={variantId}
              onChange={setVariantId}
              options={[{ value: '', label: t('providers.variantDefault') }, ...target.variants.map((v) => ({ value: v.id, label: v.name }))]}
            />
          </Field>
        )}
        {target && (
          <div>
            <div className="mb-2 text-[13px] font-medium">{t('providers.templateSwitchEndpoints')}</div>
            <ul className="grid gap-1 text-[12px]">
              {endpoints.map((e) => (
                <li key={e.protocol} className="flex gap-3">
                  <span className="w-36 shrink-0 text-[var(--text-secondary)]">{PROTOCOL_OPTIONS.find((o) => o.value === e.protocol)?.label ?? e.protocol}</span>
                  <span className="min-w-0 font-mono break-all">{e.baseUrl}</span>
                </li>
              ))}
            </ul>
          </div>
        )}
      </div>
    </Sheet>
  );
}

/**
 * Shown under an upstream 401/403 (model list, connection test) when the provider's template names key alternatives:
 * the key probably belongs to one of them, and switching keeps the key and models.
 */
export function KeyAlternativeHint({ provider, httpStatus, className }: { provider: Provider; httpStatus?: number | null; className?: string }) {
  if ((httpStatus !== 401 && httpStatus !== 403) || !provider.templateId || !canSwitchTemplate(provider)) return null;
  return <KeyAlternatives provider={provider} className={className} />;
}

function KeyAlternatives({ provider, className }: { provider: Provider; className?: string }) {
  const { t } = useI18n();
  const { toast, confirm } = useFeedback();
  const templates = useTemplates();
  const switchTemplate = useSwitchTemplate(provider.id);
  const current = templates.data?.find((x) => x.id === provider.templateId);
  const alternatives = (current?.keyAlternatives ?? []).flatMap((id) => templates.data?.filter((x) => x.id === id) ?? []);
  if (!current || alternatives.length === 0) return null;

  const go = async (alt: ProviderTemplate) => {
    const urls = [...new Set(alt.endpoints.map((e) => e.baseUrl))].join(', ');
    const ok = await confirm({
      title: t('providers.templateSwitchConfirm', { alt: alt.name }),
      detail: t('providers.templateSwitchConfirmDetail', { urls }),
      confirmLabel: t('providers.keyAlternative.switch', { alt: alt.name }),
    });
    if (!ok) return;
    switchTemplate.mutate(
      { templateId: alt.id },
      { onSuccess: () => toast(t('providers.templateSwitched', { name: alt.name }), 'success'), onError: (e) => toast(errorText(e), 'error') },
    );
  };

  return (
    <Alert className={className} tone="warning" title={t('providers.keyAlternative.title')}>
      {t('providers.keyAlternative.rejected', { name: current.name, alt: alternatives.map((a) => a.name).join(' / ') })}
      <span className="mt-2 flex flex-wrap gap-2">
        {alternatives.map((a) => (
          <Button key={a.id} size="sm" loading={switchTemplate.isPending} onClick={() => void go(a)}>
            {t('providers.keyAlternative.switch', { alt: a.name })}
          </Button>
        ))}
      </span>
    </Alert>
  );
}
