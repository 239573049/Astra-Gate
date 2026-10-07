import { PanelLeft } from 'lucide-react';
import type { ReactNode } from 'react';

import { useT } from '../../i18n';
import { isDesktop, isMac, modKey } from '../../shell/bridge';
import { cn } from '../../lib/cn';
import { Tooltip } from '../arc/tooltip/tooltip';
import { Button } from '../ui/controls';
import { TOOLBAR_RIGHT_RESERVE, useShellLayout } from './AppShell';

/**
 * A page: floating toolbar (title + capsule button groups, drag region on desktop) and scrolling
 * content that fades under the toolbar. Details, edits and additions open in dialogs (Sheet).
 */
export function Page({
  title,
  subtitle,
  actions,
  leading,
  children,
  contentClassName,
}: {
  title: ReactNode;
  subtitle?: ReactNode;
  actions?: ReactNode;
  leading?: ReactNode;
  children: ReactNode;
  contentClassName?: string;
}) {
  const t = useT();
  const layout = useShellLayout();
  const toggleLabel = layout.sidebarMode === 'expanded' ? t('shell.collapseSidebar') : t('shell.expandSidebar');

  return (
    <div className="absolute inset-0 flex flex-col">
      <header
        className="drag chrome flex h-[52px] shrink-0 items-center gap-2 pr-4 pl-2"
        style={{ paddingRight: TOOLBAR_RIGHT_RESERVE || undefined }}
      >
        <Tooltip side="bottom" content={`${toggleLabel}${isDesktop ? ` (${modKey}${isMac ? '⌥' : 'Alt+'}S)` : ''}`}>
          <Button
            variant="plain"
            icon={<PanelLeft className="size-4" />}
            aria-label={toggleLabel}
            aria-expanded={layout.sidebarMode === 'drawer' ? layout.drawerOpen : layout.sidebarMode === 'expanded'}
            onClick={layout.toggleSidebar}
          />
        </Tooltip>
        {leading}
        <div className="min-w-0">
          <h1 className="truncate text-[15px] leading-tight font-medium">{title}</h1>
          {subtitle && <div className="truncate text-[11px] text-[var(--text-secondary)]">{subtitle}</div>}
        </div>
        <div className="ml-auto flex items-center gap-2">{actions}</div>
      </header>
      <div className={cn('scroll scroll-edge min-h-0 flex-1 px-2 pt-5 pb-10', layout.sidebarMode === 'drawer' && 'px-3', contentClassName)} style={{ paddingRight: 16 }}>
        {children}
      </div>
    </div>
  );
}
