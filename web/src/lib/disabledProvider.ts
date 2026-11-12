import { ApiError } from '../api/client';
import type { DisabledProviderDetails } from '../api/types';
import type { useI18n } from '../i18n';

/** The refusal of a switched-off provider (409 with {@link DisabledProviderDetails}): which one, and where to fix it. Null for any other error. */
export function disabledProviderText(e: unknown, t: ReturnType<typeof useI18n>['t']): { title: string; hint: string } | null {
  if (!(e instanceof ApiError) || e.status !== 409) return null;
  const d = e.details as Partial<DisabledProviderDetails> | undefined;
  if (!d?.providerName) return null;
  return {
    title: t('clients.providerDisabledTitle', { name: d.providerName }),
    hint: t(d.pluginOff ? 'clients.providerDisabledPluginHint' : 'clients.providerDisabledHint'),
  };
}
