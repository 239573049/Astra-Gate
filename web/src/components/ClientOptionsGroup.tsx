import type { ClientOptionDescriptor } from '../api/types';
import { useI18n } from '../i18n';
import type { ClientOptionValues } from '../lib/clientOptions';
import { Group, Input, Row, Switch } from './ui/controls';
import { Select } from './ui/overlays';

type OptionKey = 'clients.option.context1m';

/** Client-specific options (extras.options), rendered from the descriptors the server reports for the client. */
export function ClientOptionsGroup({
  descriptors,
  values,
  onChange,
  enabled,
}: {
  descriptors: ClientOptionDescriptor[];
  values: ClientOptionValues;
  onChange: (next: ClientOptionValues) => void;
  /** The client is already applied: changes need a re-apply. */
  enabled: boolean;
}) {
  const { t } = useI18n();
  const set = (key: string, value: boolean | number | string | undefined) => {
    const next = { ...values };
    if (value === undefined || value === false || value === 0 || value === 'default' || value === '') delete next[key];
    else next[key] = value;
    onChange(next);
  };

  return (
    <Group title={t('clients.options.title')} footer={enabled ? t('clients.options.footerEnabled') : t('clients.options.footer')}>
      {descriptors.map((d) => {
        const label = t(`clients.option.${d.key}` as OptionKey);
        const detail = t(`clients.option.${d.key}.hint` as OptionKey);
        const value = values[d.key];
        return (
          <Row key={d.key} label={label} detail={detail}>
            {d.type === 'bool' && <Switch label={label} checked={value === true} onChange={(v) => set(d.key, v)} />}
            {d.type === 'int' && (
              <Input
                className="w-36"
                mono
                inputMode="numeric"
                aria-label={label}
                placeholder={t('clients.options.unset')}
                value={typeof value === 'number' ? String(value) : ''}
                onChange={(e) => {
                  const n = Number.parseInt(e.target.value.replace(/\D/g, ''), 10);
                  set(d.key, Number.isFinite(n) ? n : undefined);
                }}
              />
            )}
            {d.type === 'enum' && (
              <Select
                ariaLabel={label}
                value={typeof value === 'string' ? value : 'default'}
                onChange={(v) => set(d.key, v)}
                options={(d.choices ?? []).map((c) => ({ value: c, label: t(`clients.optionChoice.${c}` as 'clients.optionChoice.default') }))}
                className="w-36"
              />
            )}
          </Row>
        );
      })}
    </Group>
  );
}
