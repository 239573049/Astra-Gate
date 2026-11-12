import { describe, expect, it } from 'vitest';
import { ApiError } from '../api/client';
import type { useI18n } from '../i18n';
import { disabledProviderText } from '../lib/disabledProvider';

const t = ((key: string, params?: Record<string, unknown>) => (params ? `${key}(${Object.values(params).join(',')})` : key)) as ReturnType<typeof useI18n>['t'];

describe('disabledProviderText', () => {
  it('names the switched-off provider and points at the plugin when that is what is off', () => {
    const e = new ApiError('Provider "WorkBuddy" is disabled', 409, { providerId: 'plugin-workbuddy', providerName: 'WorkBuddy', pluginOff: true });
    expect(disabledProviderText(e, t)).toEqual({ title: 'clients.providerDisabledTitle(WorkBuddy)', hint: 'clients.providerDisabledPluginHint' });
    const own = new ApiError('x', 409, { providerId: 'p', providerName: 'Mine', pluginOff: false });
    expect(disabledProviderText(own, t)?.hint).toBe('clients.providerDisabledHint');
  });

  it('leaves every other error alone', () => {
    expect(disabledProviderText(new ApiError('x', 409, { boundClients: ['codex'] }), t)).toBeNull();
    expect(disabledProviderText(new ApiError('boom', 500), t)).toBeNull();
    expect(disabledProviderText(new Error('down'), t)).toBeNull();
  });
});
