import { Puzzle } from 'lucide-react';

import { api } from '../api/client';
import { errorText, Menu, useFeedback, type MenuItem } from '../components/ui/overlays';
import { useI18n } from '../i18n';
import { useContributions } from './contributions';

/**
 * The commands v2 plugins add to a host menu (ui.menus, design/plugin-v2 03 §8): one item per contribution, run with
 * `args` (what the row is: { tokenId }, { providerId } …). The first one starts a separated group. Empty outside the
 * shell or when no plugin adds to this menu.
 */
export function usePluginMenuItems(menu: string, args: Record<string, unknown>): MenuItem[] {
  const contributions = useContributions();
  const feedback = useFeedback();
  const { t } = useI18n();
  const items = contributions?.menus.filter((m) => m.menu === menu) ?? [];
  return items.map((m, i) => ({
    id: `plugin:${m.plugin}:${m.command}`,
    label: m.title,
    icon: <Puzzle className="size-3.5" />,
    separatorBefore: i === 0,
    // a plain call, not a mutation hook: host pages (and their tests' api/hooks mocks) need nothing extra for it
    onSelect: () =>
      void api<{ result: unknown }>('POST', `/api/plugins/v2/${encodeURIComponent(m.plugin)}/commands/${encodeURIComponent(m.command)}`, { args }).then(
        (r) => feedback.toast(typeof r?.result === 'string' && r.result ? r.result : t('pluginUi.commandRan', { name: m.title }), 'success'),
        (e: unknown) => feedback.toast(errorText(e), 'error'),
      ),
  }));
}

/** A host menu of its own for places with no row menu (request detail, client panel); nothing when no plugin adds to it. */
export function PluginMenu({ menu, args }: { menu: string; args: Record<string, unknown> }) {
  // nothing to add (or no shell around it): stop before the hooks that need the shell's providers
  const contributions = useContributions();
  if (!contributions?.menus.some((m) => m.menu === menu)) return null;
  return <PluginMenuButton menu={menu} args={args} />;
}

function PluginMenuButton({ menu, args }: { menu: string; args: Record<string, unknown> }) {
  const { t } = useI18n();
  const items = usePluginMenuItems(menu, args).map((it) => ({ ...it, separatorBefore: false }));
  return <Menu label={t('pluginUi.menu')} icon={<Puzzle className="size-3.5" />} items={items} />;
}
