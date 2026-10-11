import { X } from 'lucide-react';
import { useCallback, useMemo, type Ref } from 'react';

import { useClients, useProviders, useTokens } from '../api/hooks';
import type { ClientKind } from '../api/types';
import { useI18n, type MessageKey } from '../i18n';
import { MultiSelect, type MultiSelectOption } from './arc/multi-select/multi-select';
import { CLIENT_META, ClientIcon, ProviderIcon } from './icons';
import { Button, SearchField } from './ui/controls';

export const REQUEST_STATUSES = ['success', 'blocked', 'upstream_error', 'gateway_error', 'client_cancelled'] as const;

export type RequestFilterId = 'client' | 'token' | 'provider' | 'status';
/** Picked values per field; an empty list means "any". Values are query ids (client kind, token id, provider id, status). */
export type RequestFilterValues = Record<RequestFilterId, string[]>;

export const EMPTY_REQUEST_FILTERS: RequestFilterValues = { client: [], token: [], provider: [], status: [] };

const FIELD_LABEL: Record<RequestFilterId, MessageKey> = {
  client: 'requests.col.client',
  token: 'requests.col.token',
  provider: 'requests.col.provider',
  status: 'requests.col.status',
};

const FIELD_PLACEHOLDER: Record<RequestFilterId, MessageKey> = {
  client: 'requests.allClients',
  token: 'requests.allTokens',
  provider: 'requests.allProviders',
  status: 'requests.allStatus',
};

const FIELDS: RequestFilterId[] = ['client', 'token', 'provider', 'status'];

/** Request-log filter bar: the model keyword search plus one multi-select per list filter (clients carry their brand icon). */
export function RequestFilters({
  model,
  onModelChange,
  searchRef,
  filters,
  onFilterChange,
  onClear,
}: {
  model: string;
  onModelChange: (model: string) => void;
  searchRef?: Ref<HTMLInputElement>;
  filters: RequestFilterValues;
  onFilterChange: (id: RequestFilterId, values: string[]) => void;
  onClear: () => void;
}) {
  const { t } = useI18n();
  const clients = useClients();
  const providers = useProviders();
  const tokens = useTokens();

  // Options are memoized on their source data, so the memoized selects skip the page's live re-renders.
  const clientOptions = useMemo<MultiSelectOption[]>(
    () =>
      (clients.data ?? []).map((c) => ({
        value: c.kind,
        label: c.name,
        icon: c.kind in CLIENT_META ? <ClientIcon kind={c.kind as ClientKind} size={18} /> : undefined,
      })),
    [clients.data],
  );
  const providerOptions = useMemo<MultiSelectOption[]>(
    () => (providers.data ?? []).map((p) => ({ value: p.id, label: p.name, icon: <ProviderIcon name={p.name} icon={p.icon} colorKey={p.templateId} size={18} /> })),
    [providers.data],
  );
  const tokenOptions = useMemo<MultiSelectOption[]>(() => (tokens.data ?? []).map((tk) => ({ value: tk.id, label: tk.name })), [tokens.data]);
  const statusOptions = useMemo<MultiSelectOption[]>(() => REQUEST_STATUSES.map((s) => ({ value: s, label: t(`status.${s}`) })), [t]);
  const options: Record<RequestFilterId, MultiSelectOption[]> = { client: clientOptions, token: tokenOptions, provider: providerOptions, status: statusOptions };

  const onClient = useCallback((v: string[]) => onFilterChange('client', v), [onFilterChange]);
  const onToken = useCallback((v: string[]) => onFilterChange('token', v), [onFilterChange]);
  const onProvider = useCallback((v: string[]) => onFilterChange('provider', v), [onFilterChange]);
  const onStatus = useCallback((v: string[]) => onFilterChange('status', v), [onFilterChange]);
  const handlers: Record<RequestFilterId, (v: string[]) => void> = { client: onClient, token: onToken, provider: onProvider, status: onStatus };

  const active = model.trim() !== '' || FIELDS.some((id) => filters[id].length > 0);

  return (
    <div role="group" aria-label={t('requests.filters')} className="flex flex-wrap items-center gap-2">
      <SearchField
        inputRef={searchRef}
        className="min-w-[220px] flex-[2_1_240px]"
        value={model}
        onChange={onModelChange}
        placeholder={t('requests.searchModel')}
        clearLabel={t('common.clearSearch')}
      />
      {FIELDS.map((id) => (
        <MultiSelect
          key={id}
          className="w-[11.5rem] flex-[1_1_9rem]"
          label={t(FIELD_LABEL[id])}
          placeholder={t(FIELD_PLACEHOLDER[id])}
          options={options[id]}
          value={filters[id]}
          onValueChange={handlers[id]}
          clearLabel={t('requests.clearFilters')}
          emptyLabel={t('common.noResults')}
        />
      ))}
      {active && (
        <Button variant="plain" icon={<X className="size-4" />} onClick={onClear}>
          {t('requests.clearFilters')}
        </Button>
      )}
    </div>
  );
}
