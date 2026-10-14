import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { ListenStatus } from '../api/types';
import { ListenRows } from '../components/ListenSettings';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';

const spies = vi.hoisted(() => ({
  listen: undefined as unknown,
  update: vi.fn(),
  setPassword: vi.fn(),
  required: false,
}));

vi.mock('../api/hooks', () => ({
  useListen: () => ({ data: spies.listen, isPending: false, isError: false }),
  useUpdateListen: () => ({ mutate: spies.update, isPending: false }),
  useSetAdminPassword: () => ({ mutate: spies.setPassword, isPending: false }),
  useAuthStatus: () => ({ data: { required: spies.required, signedIn: true, remote: false, adminBlocked: false } }),
}));

vi.mock('motion/react', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  useReducedMotion: () => true,
}));

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const listen = (over: Partial<ListenStatus> = {}): ListenStatus => ({
  host: '127.0.0.1', port: 17321, configuredHost: '127.0.0.1', hostFromEnvironment: false, passwordSet: false, networkUrls: [], ...over,
});

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <ListenRows />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  spies.update.mockReset();
  spies.setPassword.mockReset();
  spies.required = false;
});
afterEach(cleanup);

describe('listen address settings', () => {
  it('asks for an admin password before LAN access when none is set', async () => {
    spies.listen = listen();
    spies.setPassword.mockImplementation((_password: string, opts: { onSuccess: () => void }) => opts.onSuccess());
    show();

    fireEvent.click(screen.getByRole('button', { name: t('settings.listen.lan') }));
    await screen.findByText(t('settings.listen.passwordTitle'));
    const submit = screen.getByRole('button', { name: t('settings.listen.passwordAction') });
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByLabelText(t('settings.remote.passwordNew')), { target: { value: 'correct-horse' } });
    fireEvent.change(screen.getByLabelText(t('settings.listen.passwordRepeat')), { target: { value: 'correct-hors' } });
    expect(screen.getByText(t('settings.listen.passwordMismatch'))).toBeInTheDocument();
    expect(submit).toBeDisabled();

    fireEvent.change(screen.getByLabelText(t('settings.listen.passwordRepeat')), { target: { value: 'correct-horse' } });
    fireEvent.click(submit);
    expect(spies.setPassword).toHaveBeenCalledWith('correct-horse', expect.anything());
    expect(spies.update).toHaveBeenCalledWith('0.0.0.0', expect.anything());
  });

  it('confirms LAN access when a password exists, and does not save on cancel', async () => {
    spies.listen = listen({ passwordSet: true });
    show();

    fireEvent.click(screen.getByRole('button', { name: t('settings.listen.lan') }));
    fireEvent.click(await screen.findByRole('button', { name: t('common.cancel') }));
    await waitFor(() => expect(screen.queryByText(t('settings.listen.lanConfirm'))).not.toBeInTheDocument());
    expect(spies.update).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: t('settings.listen.lan') }));
    fireEvent.click(await screen.findByRole('button', { name: t('settings.listen.lanAction') }));
    await waitFor(() => expect(spies.update).toHaveBeenCalledWith('0.0.0.0', expect.anything()));
  });

  it('switches back to this machine without asking', () => {
    spies.listen = listen({ host: '0.0.0.0', configuredHost: '0.0.0.0', passwordSet: true, networkUrls: ['http://192.168.1.10:17321'] });
    show();

    expect(screen.getByText('http://192.168.1.10:17321/v1')).toBeInTheDocument();
    expect(screen.getByText(t('settings.remote.password'))).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: t('settings.listen.local') }));
    expect(spies.update).toHaveBeenCalledWith('127.0.0.1', expect.anything());
  });

  it('points the browser at astra restart while a change is pending', () => {
    spies.listen = listen({ configuredHost: '0.0.0.0', passwordSet: true });
    show();

    expect(screen.getByText(`${t('settings.listen.pending', { host: '0.0.0.0' })} · ${t('settings.listen.restartHint')}`)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: t('settings.listen.restart') })).not.toBeInTheDocument();
  });

  it('offers no switch when ASTRA_HOST decides or another device is asking', () => {
    spies.listen = listen({ host: '0.0.0.0', configuredHost: '0.0.0.0', hostFromEnvironment: true, passwordSet: true });
    show();
    expect(screen.getByText(t('settings.listen.env', { address: '0.0.0.0:17321' }))).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: t('settings.listen.local') })).not.toBeInTheDocument();
    cleanup();

    spies.listen = listen({ host: '0.0.0.0', configuredHost: '0.0.0.0', passwordSet: true });
    spies.required = true;
    show();
    expect(screen.getByText(t('settings.listen.localOnly', { address: '0.0.0.0:17321' }))).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: t('settings.listen.lan') })).not.toBeInTheDocument();
  });
});
