import { AppWindow, Boxes, LayoutDashboard, ScrollText, Server, Settings as SettingsIcon, ShieldCheck } from 'lucide-react';
import type { ReactNode } from 'react';

import type { NavTarget } from '../../shell/bridge';
import type { MessageKey } from '../../i18n';

export interface NavItem {
  id: NavTarget;
  path: string;
  label: MessageKey;
  color: string;
  icon: ReactNode;
}

const ic = 'size-4';

export const NAV_GROUPS: { title?: MessageKey; items: NavItem[] }[] = [
  {
    items: [
      { id: 'overview', path: '/', label: 'nav.overview', color: '#0a84ff', icon: <LayoutDashboard className={ic} strokeWidth={1.75} /> },
      { id: 'requests', path: '/requests', label: 'nav.requests', color: '#8e8e93', icon: <ScrollText className={ic} strokeWidth={1.75} /> },
    ],
  },
  {
    title: 'nav.group.config',
    items: [
      { id: 'clients', path: '/clients', label: 'nav.clients', color: '#5e5ce6', icon: <AppWindow className={ic} strokeWidth={1.75} /> },
      { id: 'providers', path: '/providers', label: 'nav.providers', color: '#30b0c7', icon: <Server className={ic} strokeWidth={1.75} /> },
      { id: 'models', path: '/models', label: 'nav.models', color: '#ff9f0a', icon: <Boxes className={ic} strokeWidth={1.75} /> },
      { id: 'privacy', path: '/privacy', label: 'nav.privacy', color: '#30d158', icon: <ShieldCheck className={ic} strokeWidth={1.75} /> },
    ],
  },
  {
    title: 'nav.group.system',
    items: [{ id: 'settings', path: '/settings', label: 'nav.settings', color: '#8e8e93', icon: <SettingsIcon className={ic} strokeWidth={1.75} /> }],
  },
];

export const NAV_PATHS: Record<NavTarget, string> = Object.fromEntries(
  NAV_GROUPS.flatMap((g) => g.items).map((i) => [i.id, i.path]),
) as Record<NavTarget, string>;
