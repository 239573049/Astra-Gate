import { useQueryClient } from '@tanstack/react-query';
import { LogIn } from 'lucide-react';
import { useState } from 'react';

import { keys } from '../api/hooks';
import type { Provider } from '../api/types';
import { Button } from '../components/ui/controls';
import { Sheet } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { useContributions } from './contributions';
import { PluginView } from './PluginView';

/** A v2 provider row's plugin facts (settings.plugin, design/plugin-v2 08 §2), or null for any other row. */
export function v2ProviderOf(p: Provider): { plugin: string; provider: string; signIn?: string | null } | null {
  const s = (p.settings?.plugin ?? null) as { v2?: boolean; plugin?: string; provider?: string; signIn?: string | null } | null;
  return s?.v2 && s.plugin && s.provider ? { plugin: s.plugin, provider: s.provider, signIn: s.signIn } : null;
}

/**
 * A v2 provider's sign-in (ui.flows, 08 §4): the plugin's own flow view in a sheet, with { flow, provider, account? }. When
 * it closes, the provider's accounts are read again (the flow added one through providers.accounts.add).
 */
export function PluginFlowButton({ provider, account, label }: { provider: Provider; account?: string; label?: string }) {
  const { t } = useI18n();
  const contributions = useContributions();
  const qc = useQueryClient();
  const [open, setOpen] = useState(false);
  const v2 = v2ProviderOf(provider);
  const flow = v2?.signIn ? contributions?.flows?.find((f) => f.plugin === v2.plugin && f.id === v2.signIn) : undefined;
  if (!v2 || !flow) return null;
  const close = () => {
    setOpen(false);
    void qc.invalidateQueries({ queryKey: keys.providerAccounts(provider.id) });
  };
  return (
    <>
      <Button variant="primary" icon={<LogIn className="size-3.5" />} onClick={() => setOpen(true)}>
        {label ?? t('pluginUi.flow.signIn')}
      </Button>
      <Sheet open={open} onOpenChange={(o) => (o ? setOpen(true) : close())} title={flow.title} width={520}>
        {open && (
          <PluginView
            target={{ plugin: flow.plugin, view: flow.view, params: { flow: flow.id, provider: v2.provider, ...(account ? { account } : {}) } }}
            surface="sheet"
            onClose={close}
          />
        )}
      </Sheet>
    </>
  );
}
