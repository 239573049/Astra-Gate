import type { ApiProtocol } from '../api/types';
import { useI18n } from '../i18n';
import { autoProtocol, type ProtocolModel } from '../lib/clientProtocols';
import { PROTOCOL_OPTIONS } from '../pages/ProvidersPage';
import { Group, Row } from './ui/controls';
import { Select } from './ui/overlays';

const AUTO = 'auto';
const protocolName = (p: ApiProtocol) => PROTOCOL_OPTIONS.find((o) => o.value === p)?.label ?? p;

/**
 * The protocol of each model a client lists (extras.models): automatic — its native protocol when the client speaks it —
 * or the user's choice (extras.protocolOverrides). Shown for clients that can use more than one protocol.
 */
export function ClientProtocolsGroup({
  models,
  supported,
  values,
  onChange,
  enabled,
}: {
  models: ProtocolModel[];
  /** The protocols this client can be given, its default first. */
  supported: ApiProtocol[];
  values: Record<string, ApiProtocol>;
  onChange: (next: Record<string, ApiProtocol>) => void;
  /** The client is already applied: changes need a re-apply. */
  enabled: boolean;
}) {
  const { t } = useI18n();
  const set = (model: string, value: string) => {
    const next = { ...values };
    if (value === AUTO) delete next[model];
    else next[model] = value as ApiProtocol;
    onChange(next);
  };

  return (
    <Group title={t('clients.protocols.title')} footer={enabled ? t('clients.protocols.footerEnabled') : t('clients.protocols.footer')}>
      {models.map((m) => {
        const auto = autoProtocol(m, supported);
        const native = m.native.filter((p) => supported.includes(p));
        return (
          <Row
            key={m.id}
            label={m.name}
            detail={native.length > 0 ? t('clients.protocols.native', { protocols: native.map(protocolName).join(' · ') }) : t('clients.protocols.noNative')}
          >
            <Select
              ariaLabel={m.name}
              className="w-60"
              value={values[m.id] && supported.includes(values[m.id]) ? values[m.id] : AUTO}
              onChange={(v) => set(m.id, v)}
              options={[
                {
                  value: AUTO,
                  label: t(auto.translated ? 'clients.protocols.autoTranslated' : 'clients.protocols.auto', { protocol: protocolName(auto.protocol) }),
                },
                ...supported.map((p) => ({ value: p, label: protocolName(p) })),
              ]}
            />
          </Row>
        );
      })}
    </Group>
  );
}
