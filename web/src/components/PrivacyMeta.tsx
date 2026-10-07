import type { MessageKey } from '../i18n';
import { useI18n } from '../i18n';
import type { PrivacyAction } from '../api/types';
import { Badge } from './ui/controls';

export const PRIVACY_CATEGORY_KEY: Record<string, MessageKey> = {
  api_key: 'settings.privacy.category.api_key',
  aws_key: 'settings.privacy.category.aws_key',
  jwt: 'settings.privacy.category.jwt',
  private_key: 'settings.privacy.category.private_key',
  email: 'settings.privacy.category.email',
  phone: 'settings.privacy.category.phone',
  intranet: 'settings.privacy.category.intranet',
  credit_card: 'settings.privacy.category.credit_card',
  national_id: 'settings.privacy.category.national_id',
  credential: 'settings.privacy.category.credential',
  custom: 'settings.privacy.category.custom',
};

export const PRIVACY_ACTION_KEY: Record<string, MessageKey> = {
  warn: 'settings.privacy.action.warn',
  block: 'settings.privacy.action.block',
  redact: 'settings.privacy.action.redact',
};

/** Localized category name; unknown categories (e.g. removed custom rules) fall back to the raw id. */
export function usePrivacyCategoryLabel() {
  const { t } = useI18n();
  return (category: string) => {
    const key = PRIVACY_CATEGORY_KEY[category];
    return key ? t(key) : category;
  };
}

export function PrivacyActionBadge({ action }: { action: Exclude<PrivacyAction, 'off'> }) {
  const { t } = useI18n();
  return (
    <Badge tone={action === 'block' ? 'red' : action === 'redact' ? 'orange' : 'neutral'}>
      {t(PRIVACY_ACTION_KEY[action] ?? 'settings.privacy.action.warn')}
    </Badge>
  );
}
