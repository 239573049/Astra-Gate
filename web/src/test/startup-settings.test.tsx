import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { AutostartStatus } from '../api/types';
import { StartupSettings } from '../components/StartupSettings';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';
import type { LoginItemState } from '../shell/bridge';

const spies = vi.hoisted(() => ({
  autostart: undefined as unknown,
  update: vi.fn(),
  bridge: undefined as unknown,
}));

vi.mock('../api/hooks', () => ({
  useAutostart: () => ({ data: spies.autostart, isPending: false, isError: false }),
  useSetAutostart: () => ({ mutate: spies.update, isPending: false }),
}));

vi.mock('../shell/bridge', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getBridge: () => spies.bridge,
  isMac: false,
}));

vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const status = (over: Partial<AutostartStatus> = {}): AutostartStatus => ({
  supported: true,
  enabled: false,
  mechanism: 'systemd',
  location: '/home/demo/.config/systemd/user/astra.service',
  canChange: true,
  ...over,
});

/** A desktop bridge whose login item answers `set` with `answer` (default: what was asked). */
function desktop(initial: LoginItemState, answer?: (enabled: boolean) => LoginItemState) {
  let listener: ((s: LoginItemState) => void) | null = null;
  const loginItem = {
    get: vi.fn(() => Promise.resolve(initial)),
    set: vi.fn((enabled: boolean) => Promise.resolve(answer?.(enabled) ?? { supported: true, enabled })),
    onChanged: vi.fn((cb: (s: LoginItemState) => void) => {
      listener = cb;
      return () => {
        listener = null;
      };
    }),
  };
  spies.bridge = { loginItem };
  return { loginItem, push: (s: LoginItemState) => listener?.(s) };
}

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <StartupSettings />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  spies.update.mockReset();
  spies.bridge = undefined;
});
afterEach(cleanup);

describe('start at login settings', () => {
  it('switches the background service in a browser, without a desktop row', () => {
    spies.autostart = status();
    show();

    expect(screen.queryByRole('switch', { name: t('settings.startup.app') })).not.toBeInTheDocument();
    expect(screen.getByText(t('settings.startup.service.systemd'))).toBeInTheDocument();
    const toggle = screen.getByRole('switch', { name: t('settings.startup.service') });
    expect(toggle).not.toBeChecked();
    fireEvent.click(toggle);
    expect(spies.update).toHaveBeenCalledWith(true, expect.anything());
  });

  it('shows where the entry lives once it is on', () => {
    spies.autostart = status({ enabled: true });
    show();

    expect(screen.getByRole('switch', { name: t('settings.startup.service') })).toBeChecked();
    expect(screen.getByText(t('settings.startup.entry', { location: '/home/demo/.config/systemd/user/astra.service' }))).toBeInTheDocument();
  });

  it('leaves the switch to the computer running Astra', () => {
    spies.autostart = status({ canChange: false });
    show();

    expect(screen.getByRole('switch', { name: t('settings.startup.service') })).toBeDisabled();
    expect(screen.getByText(t('settings.startup.localOnly'))).toBeInTheDocument();
  });

  it('explains why there is nothing to switch', () => {
    spies.autostart = status({ supported: false, mechanism: 'none', unsupported: 'container', location: null });
    show();

    expect(screen.queryByRole('switch', { name: t('settings.startup.service') })).not.toBeInTheDocument();
    expect(screen.getByText(t('settings.startup.unsupported.container'))).toBeInTheDocument();
  });

  it('switches the desktop app login item and follows the tray menu', async () => {
    spies.autostart = status({ mechanism: 'launchd' });
    const { loginItem, push } = desktop({ supported: true, enabled: false });
    show();

    const toggle = await screen.findByRole('switch', { name: t('settings.startup.app') });
    expect(toggle).not.toBeChecked();
    fireEvent.click(toggle);
    expect(loginItem.set).toHaveBeenCalledWith(true);
    await waitFor(() => expect(toggle).toBeChecked());

    // Turned off from the tray menu.
    act(() => push({ supported: true, enabled: false }));
    expect(toggle).not.toBeChecked();
  });

  it('says so when the system refuses the login item', async () => {
    spies.autostart = status();
    desktop({ supported: true, enabled: false }, () => ({ supported: true, enabled: false }));
    show();

    fireEvent.click(await screen.findByRole('switch', { name: t('settings.startup.app') }));
    expect(await screen.findByText(t('settings.startup.failed'))).toBeInTheDocument();
  });

  it('disables the desktop row in a dev run', async () => {
    spies.autostart = status();
    desktop({ supported: false, enabled: false });
    show();

    expect(await screen.findByRole('switch', { name: t('settings.startup.app') })).toBeDisabled();
    expect(screen.getByText(t('settings.startup.appUnsupported'))).toBeInTheDocument();
  });
});
