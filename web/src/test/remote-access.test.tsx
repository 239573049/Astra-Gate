import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { TunnelStatus } from '../api/types';
import { friendlyTunnelError, RemoteAccessSettings } from '../components/RemoteAccessSettings';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';

const spies = vi.hoisted(() => ({
  toggle: vi.fn(),
  update: vi.fn(),
  tunnel: undefined as unknown,
  remote: false,
  detect: vi.fn(),
}));

vi.mock('../api/hooks', () => ({
  useTunnel: () => ({ data: spies.tunnel }),
  useToggleTunnel: () => ({ mutate: spies.toggle, isPending: false }),
  useUpdateTunnel: () => ({ mutate: spies.update, isPending: false }),
  useSetAdminPassword: () => ({ mutate: vi.fn(), isPending: false }),
  useDetectCloudflared: () => ({ mutate: spies.detect, isPending: false }),
  useAuthStatus: () => ({ data: { required: spies.remote, signedIn: true, remote: spies.remote, adminBlocked: false } }),
}));

class NoopResizeObserver {
  observe() {}
  unobserve() {}
  disconnect() {}
}
vi.stubGlobal('ResizeObserver', NoopResizeObserver);

vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const status = (over: Partial<TunnelStatus> = {}): TunnelStatus => ({
  supported: true, enabled: false, state: 'off', mode: 'quick', hasToken: false, allowAdmin: false,
  originUrl: 'http://127.0.0.1:17321',
  cloudflared: { path: '/x/cloudflared', version: '2026.10.0', downloading: false, asset: 'cloudflared-darwin-arm64.tgz' },
  download: { downloadSource: 'auto' },
  checks: { passwordSet: true }, log: [], ...over,
});

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <RemoteAccessSettings />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  // Radix select needs pointer capture and scrolling, which jsdom lacks.
  Element.prototype.hasPointerCapture = () => false;
  Element.prototype.setPointerCapture = () => {};
  Element.prototype.releasePointerCapture = () => {};
  Element.prototype.scrollIntoView = () => {};
  spies.toggle.mockReset();
  spies.update.mockReset();
  spies.detect.mockReset();
  spies.remote = false;
  localStorage.removeItem('astra.remote.acknowledged');
});
afterEach(cleanup);

describe('remote access settings', () => {
  it('asks before the first switch-on only', async () => {
    spies.tunnel = status();
    show();
    fireEvent.click(screen.getByRole('switch', { name: t('settings.remote.enable') }));
    fireEvent.click(await screen.findByRole('button', { name: t('settings.remote.enableAction') }));
    await waitFor(() => expect(spies.toggle).toHaveBeenCalledWith(true, expect.anything()));
    cleanup();

    spies.toggle.mockReset();
    show();
    fireEvent.click(screen.getByRole('switch', { name: t('settings.remote.enable') }));
    await waitFor(() => expect(spies.toggle).toHaveBeenCalledWith(true, expect.anything()));
    expect(screen.queryByText(t('settings.remote.enableConfirm'))).toBeNull();
  });

  it('shows the connection time and the remote address while connected', () => {
    spies.tunnel = status({
      enabled: true, state: 'connected', publicUrl: 'https://astra.example.com',
      connectedAt: new Date(Date.now() - 12 * 60_000).toISOString(),
    });
    show();
    expect(screen.getByText(/Connected for 12 minutes/)).toBeInTheDocument();
    expect(screen.getByText('https://astra.example.com/v1')).toBeInTheDocument();
  });

  it('confirms a mode switch while running, and says when it stops the tunnel', async () => {
    spies.tunnel = status({ enabled: true, state: 'connected', publicUrl: 'https://a.trycloudflare.com' });
    show();
    fireEvent.click(screen.getByRole('button', { name: t('settings.remote.mode.named') }));
    expect(await screen.findByText(t('settings.remote.modeSwitchStopDetail'))).toBeInTheDocument();
    expect(spies.update).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: t('settings.remote.modeSwitchAction') }));
    await waitFor(() => expect(spies.update).toHaveBeenCalledWith({ mode: 'named' }, expect.anything()));
  });

  it('switches the mode at once while off', async () => {
    spies.tunnel = status();
    show();
    fireEvent.click(screen.getByRole('button', { name: t('settings.remote.mode.named') }));
    await waitFor(() => expect(spies.update).toHaveBeenCalledWith({ mode: 'named' }, expect.anything()));
  });

  it('warns a remote admin before they lock themselves out', async () => {
    spies.remote = true;
    spies.tunnel = status({ enabled: true, state: 'connected', allowAdmin: true, publicUrl: 'https://astra.example.com' });
    show();
    fireEvent.click(screen.getByRole('switch', { name: t('settings.remote.scope.admin') }));
    expect(await screen.findByText(t('settings.remote.adminOffSelfDetail'))).toBeInTheDocument();
    // The password can't be changed through a tunnel, so it isn't offered there.
    expect(screen.queryByRole('button', { name: t('settings.remote.passwordChange') })).toBeNull();
  });

  it('saves a custom download source together with its address', async () => {
    spies.tunnel = status();
    show();
    fireEvent.pointerDown(screen.getByRole('combobox', { name: t('settings.remote.cf.source') }), { button: 0, ctrlKey: false, pointerType: 'mouse' });
    fireEvent.click(await screen.findByRole('option', { name: t('settings.remote.cf.source.custom') }));
    // Not saved yet: a custom source without its address would have nothing to download from.
    expect(spies.update).not.toHaveBeenCalled();
    fireEvent.change(screen.getByLabelText(t('settings.remote.cf.baseUrl')), { target: { value: ' https://proxy.test/gh ' } });
    fireEvent.click(screen.getAllByRole('button', { name: t('common.save') }).at(-1)!);
    expect(spies.update).toHaveBeenCalledWith({ downloadSource: 'custom', downloadBaseUrl: 'https://proxy.test/gh' }, expect.anything());
  });

  it('finds an installed cloudflared and uses it', async () => {
    spies.detect.mockImplementation((_: unknown, opts: { onSuccess: (r: { path: string }) => void }) => opts.onSuccess({ path: '/opt/homebrew/bin/cloudflared' }));
    spies.tunnel = status({ cloudflared: { downloading: false, asset: 'cloudflared-darwin-arm64.tgz', version: '2026.10.0' } });
    show();
    fireEvent.click(screen.getByRole('button', { name: t('settings.remote.cf.choose') }));
    expect(await screen.findByDisplayValue('/opt/homebrew/bin/cloudflared')).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: t('settings.remote.cf.use') }));
    expect(spies.update).toHaveBeenCalledWith({ cloudflaredPath: '/opt/homebrew/bin/cloudflared' }, expect.anything());
  });

  it('keeps the program choice off the page through a tunnel', () => {
    spies.remote = true;
    spies.tunnel = status({ enabled: true, state: 'connected', allowAdmin: true, publicUrl: 'https://astra.example.com' });
    show();
    expect(screen.queryByRole('button', { name: t('settings.remote.cf.choose') })).toBeNull();
    expect(screen.getByRole('combobox', { name: t('settings.remote.cf.source') })).toBeInTheDocument();
  });

  it('turns cloudflared errors into advice', () => {
    const s = { originUrl: 'http://127.0.0.1:17321' };
    expect(friendlyTunnelError('ERR Unauthorized: Invalid tunnel secret', s, t)).toBe(t('settings.remote.err.token'));
    expect(friendlyTunnelError('downloading cloudflared 2026.10.0: 404', s, t)).toBe(t('settings.remote.err.download'));
    expect(friendlyTunnelError('Unable to reach the origin service. The service may be down', s, t)).toBe(
      t('settings.remote.err.origin', { origin: 'http://127.0.0.1:17321' }),
    );
    expect(friendlyTunnelError('failed to dial to edge with quic: timeout', s, t)).toBe(t('settings.remote.err.network'));
    expect(friendlyTunnelError('429 Too Many Requests', s, t)).toBe(t('settings.remote.err.rate'));
    expect(friendlyTunnelError('something else', s, t)).toBe(t('settings.remote.err.generic'));
  });
});
