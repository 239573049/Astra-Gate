import { useQueryClient } from '@tanstack/react-query';
import { AnimatePresence, motion, type Transition } from 'motion/react';
import { createContext, useCallback, useContext, useEffect, useMemo, useState, type ReactNode } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router';

import { useHealth, useSettings } from '../../api/hooks';
import { useT } from '../../i18n';
import { cn } from '../../lib/cn';
import { useMediaQuery } from '../../lib/hooks';
import { isDesktop, isMac, platform, sidebarRailWidth } from '../../shell/bridge';
import { emitCommand, useCommands } from '../../shell/commands';
import { systemActions } from '../../shell/systemActions';
import { AstraLogo } from '../AstraLogo';
import { Button, Dot, Tile } from '../ui/controls';
import { useFeedback } from '../ui/overlays';
import { ConnectionBanner } from './ConnectionBanner';
import { NAV_GROUPS, NAV_PATHS } from './nav';

export const SIDEBAR_EXPANDED = 224;
/** Collapsed rail width; wider on macOS desktop where the traffic lights live inside it. */
export const SIDEBAR_RAIL = sidebarRailWidth();
/** Inset of floating glass panels from the window edge. */
export const PANEL_INSET = 8;
/** Icons keep the same x position in both states, so only the labels move during the animation. */
const ITEM_PAD = (SIDEBAR_RAIL - 16 - 22) / 2;

const SIDEBAR_SPRING: Transition = { type: 'spring', stiffness: 380, damping: 38, mass: 0.9 };
const SIDEBAR_KEY = 'astra.sidebar';

type SidebarMode = 'expanded' | 'rail' | 'drawer';

interface ShellLayout {
  sidebarMode: SidebarMode;
  sidebarWidth: number;
  drawerOpen: boolean;
  setDrawerOpen: (open: boolean) => void;
  /** Expanded ⇄ icon rail (drawer open/close on narrow browser windows). */
  toggleSidebar: () => void;
  narrow: boolean;
}

const LayoutContext = createContext<ShellLayout | null>(null);

export function useShellLayout(): ShellLayout {
  const ctx = useContext(LayoutContext);
  if (!ctx) throw new Error('useShellLayout outside AppShell');
  return ctx;
}

function readSidebarPref(): boolean | null {
  try {
    const v = localStorage.getItem(SIDEBAR_KEY);
    return v === 'collapsed' ? true : v === 'expanded' ? false : null;
  } catch {
    return null;
  }
}

/** SVG displacement filter used by `:root.refract .glass` (Chromium liquid-glass refraction). */
function RefractionFilter() {
  return (
    <svg width="0" height="0" aria-hidden className="absolute">
      <filter id="astra-refract" x="0" y="0" width="100%" height="100%" colorInterpolationFilters="sRGB">
        <feTurbulence type="fractalNoise" baseFrequency="0.006 0.009" numOctaves="2" seed="11" result="noise" />
        <feGaussianBlur in="noise" stdDeviation="2.5" result="soft" />
        <feDisplacementMap in="SourceGraphic" in2="soft" scale="14" xChannelSelector="R" yChannelSelector="G" />
      </filter>
    </svg>
  );
}

export function AppShell() {
  const navigate = useNavigate();
  const location = useLocation();
  const qc = useQueryClient();
  const narrow = useMediaQuery('(max-width: 767px)');
  const medium = useMediaQuery('(max-width: 1099px)');
  // null = no explicit choice yet: follow the window width (browser only).
  const [collapsedPref, setCollapsedPref] = useState<boolean | null>(readSidebarPref);
  const [drawerOpen, setDrawerOpen] = useState(false);

  const collapsed = collapsedPref ?? (!isDesktop && medium);
  const sidebarMode: SidebarMode = !isDesktop && narrow ? 'drawer' : collapsed ? 'rail' : 'expanded';
  const sidebarWidth = sidebarMode === 'expanded' ? SIDEBAR_EXPANDED : sidebarMode === 'rail' ? SIDEBAR_RAIL : 0;

  useEffect(() => setDrawerOpen(false), [location.pathname]);

  const toggleSidebar = useCallback(() => {
    if (sidebarMode === 'drawer') {
      setDrawerOpen((o) => !o);
      return;
    }
    const next = !collapsed;
    setCollapsedPref(next);
    try {
      localStorage.setItem(SIDEBAR_KEY, next ? 'collapsed' : 'expanded');
    } catch {
      // ignore
    }
  }, [sidebarMode, collapsed]);

  useCommands(
    useCallback(
      (cmd) => {
        if (cmd.startsWith('nav:')) {
          navigate(NAV_PATHS[cmd.slice(4) as keyof typeof NAV_PATHS]);
          return;
        }
        switch (cmd) {
          case 'toggle-sidebar':
            toggleSidebar();
            return;
          case 'settings:tray':
            navigate('/settings/tray');
            return;
          case 'refresh':
            void qc.invalidateQueries();
            return;
          case 'new-provider':
            if (!location.pathname.startsWith('/providers')) navigate('/providers?new=1');
            else emitCommand(cmd);
            return;
          default:
            emitCommand(cmd);
        }
      },
      [navigate, qc, toggleSidebar, location.pathname],
    ),
  );

  const layout = useMemo<ShellLayout>(
    () => ({ sidebarMode, sidebarWidth, drawerOpen, setDrawerOpen, toggleSidebar, narrow: !isDesktop && narrow }),
    [sidebarMode, sidebarWidth, drawerOpen, toggleSidebar, narrow],
  );

  return (
    <LayoutContext.Provider value={layout}>
      <div className="wallpaper relative h-full overflow-hidden">
        <RefractionFilter />
        {sidebarMode !== 'drawer' && <Sidebar rail={sidebarMode === 'rail'} />}
        <AnimatePresence>
          {sidebarMode === 'drawer' && drawerOpen && (
            <>
              <motion.div
                className="fixed inset-0 z-30 bg-black/20"
                initial={{ opacity: 0 }}
                animate={{ opacity: 1 }}
                exit={{ opacity: 0 }}
                onClick={() => setDrawerOpen(false)}
              />
              <motion.div
                className="fixed inset-y-0 left-0 z-40"
                style={{ width: SIDEBAR_EXPANDED + PANEL_INSET }}
                initial={{ x: -SIDEBAR_EXPANDED - PANEL_INSET }}
                animate={{ x: 0 }}
                exit={{ x: -SIDEBAR_EXPANDED - PANEL_INSET }}
                transition={SIDEBAR_SPRING}
              >
                <Sidebar rail={false} />
              </motion.div>
            </>
          )}
        </AnimatePresence>
        {/* The content area starts to the right of the sidebar (it must never cover it, or the menu is unclickable). */}
        <motion.main
          className="absolute inset-y-0 right-0 flex flex-col"
          initial={false}
          animate={{ left: sidebarWidth ? sidebarWidth + PANEL_INSET : 0 }}
          transition={SIDEBAR_SPRING}
        >
          <ConnectionBanner />
          <div className="relative min-h-0 flex-1">
            <Outlet />
          </div>
        </motion.main>
      </div>
    </LayoutContext.Provider>
  );
}

/** Fades labels in/out while the panel width animates (icons stay put). */
function Label({ hidden, children, className }: { hidden: boolean; children: ReactNode; className?: string }) {
  return (
    <motion.span
      initial={false}
      animate={{ opacity: hidden ? 0 : 1, x: hidden ? -6 : 0 }}
      transition={{ duration: hidden ? 0.12 : 0.22, delay: hidden ? 0 : 0.08 }}
      className={cn('relative truncate whitespace-nowrap', className)}
      aria-hidden={hidden || undefined}
    >
      {children}
    </motion.span>
  );
}

function Sidebar({ rail }: { rail: boolean }) {
  const t = useT();
  // Traffic lights live inside the sidebar on macOS desktop; elsewhere this row shows the logo.
  const trafficLights = isDesktop && isMac;
  return (
    <motion.nav
      aria-label="Astra"
      className="glass chrome absolute z-20 flex flex-col overflow-hidden rounded-[18px]"
      style={{ top: PANEL_INSET, left: PANEL_INSET, bottom: PANEL_INSET }}
      initial={false}
      animate={{ width: rail ? SIDEBAR_RAIL : SIDEBAR_EXPANDED }}
      transition={SIDEBAR_SPRING}
    >
      <div className="drag flex h-[52px] shrink-0 items-center gap-2" style={{ paddingLeft: (SIDEBAR_RAIL - 24) / 2 }}>
        {!trafficLights && (
          <>
            <AstraLogo className="size-6 shrink-0 rounded-[6px]" />
            <Label hidden={rail} className="text-[14px] font-medium">
              Astra
            </Label>
          </>
        )}
      </div>
      <div className="scroll min-h-0 flex-1 overflow-x-hidden px-2 pb-2">
        {NAV_GROUPS.map((g, gi) => (
          <div key={gi}>
            {g.title && (
              <div className="relative mt-2 h-6">
                <Label hidden={rail} className="absolute bottom-1 left-3 text-[11px] font-medium text-[var(--text-muted)]">
                  {t(g.title)}
                </Label>
                <motion.div
                  initial={false}
                  animate={{ opacity: rail ? 1 : 0, scaleX: rail ? 1 : 0.4 }}
                  className="absolute top-1/2 h-px bg-[var(--border)]"
                  style={{ left: ITEM_PAD, width: 22 }}
                />
              </div>
            )}
            {g.items.map((item) => (
              <NavLink
                key={item.id}
                to={item.path}
                end={item.path === '/'}
                title={rail ? t(item.label) : undefined}
                aria-label={t(item.label)}
                className={({ isActive }) =>
                  cn('relative my-px flex h-8 items-center gap-2.5 rounded-full text-[13px] transition-colors', !isActive && 'hover:bg-[var(--surface-muted)]')
                }
                style={{ paddingLeft: ITEM_PAD, paddingRight: 10 }}
              >
                {({ isActive }) => (
                  <>
                    {isActive && (
                      <motion.span
                        layoutId="sidebar-selection"
                        transition={{ type: 'spring', stiffness: 420, damping: 34 }}
                        className="absolute inset-0 rounded-full bg-[color-mix(in_oklab,var(--accent)_18%,transparent)]"
                      />
                    )}
                    <Tile size={22} className={cn('relative', isActive && 'text-[var(--accent)]')}>
                      {item.icon}
                    </Tile>
                    <Label hidden={rail}>{t(item.label)}</Label>
                  </>
                )}
              </NavLink>
            ))}
          </div>
        ))}
      </div>
      <ServiceStatus rail={rail} />
    </motion.nav>
  );
}

function ServiceStatus({ rail }: { rail: boolean }) {
  const t = useT();
  const health = useHealth();
  const settings = useSettings();
  const { toast } = useFeedback();
  const [starting, setStarting] = useState(false);
  const up = health.isSuccess && !health.isError;
  const address = settings.data?.gatewayBaseUrl?.replace(/^https?:\/\//, '');
  const status = up ? t('shell.running') : health.isLoading ? t('shell.connecting') : t('shell.disconnected');

  const start = async () => {
    setStarting(true);
    const r = await systemActions.startService();
    setStarting(false);
    if (r && !r.ok) toast(r.error || t('shell.startFailed'), 'error');
    void health.refetch();
  };

  return (
    <div className="relative m-2 min-h-10 shrink-0">
      <AnimatePresence initial={false} mode="popLayout">
        {rail ? (
          <motion.div
            key="dot"
            initial={{ opacity: 0, scale: 0.6 }}
            animate={{ opacity: 1, scale: 1 }}
            exit={{ opacity: 0, scale: 0.6 }}
            className="flex h-10 items-center"
            style={{ paddingLeft: ITEM_PAD + 7 }}
            title={status}
          >
            <Dot tone={up ? 'green' : 'red'} />
          </motion.div>
        ) : (
          <motion.div
            key="card"
            initial={{ opacity: 0 }}
            animate={{ opacity: 1, transition: { delay: 0.1 } }}
            exit={{ opacity: 0, transition: { duration: 0.1 } }}
            className="rounded-xl bg-[color-mix(in_oklab,var(--glass-tint)_45%,transparent)] px-3 py-2 text-[11px] whitespace-nowrap text-[var(--text-secondary)]"
            style={{ width: SIDEBAR_EXPANDED - 16 }}
          >
            <div className="flex items-center gap-1.5">
              <Dot tone={up ? 'green' : health.isLoading ? 'neutral' : 'red'} />
              <span className="truncate">{status}</span>
            </div>
            {up && address && <div className="mt-0.5 truncate font-mono text-[10.5px] text-[var(--text-muted)] selectable">{address}</div>}
            {!up && !health.isLoading && isDesktop && (
              <Button size="sm" variant="primary" className="mt-1.5 w-full" loading={starting} onClick={() => void start()}>
                {t('shell.startService')}
              </Button>
            )}
          </motion.div>
        )}
      </AnimatePresence>
    </div>
  );
}

/** Right padding the toolbar keeps free for the Windows caption buttons (titleBarOverlay). */
export const TOOLBAR_RIGHT_RESERVE = isDesktop && platform === 'win32' ? 140 : 0;
