import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { UpdateStatus } from '../api/types';
import { FeedbackProvider } from '../components/ui/overlays';
import { I18nProvider, translate } from '../i18n';
import { UpdateRows } from '../components/UpdateRows';

const spies = vi.hoisted(() => ({
  status: undefined as unknown,
  apply: vi.fn(),
  patch: vi.fn(),
  bridge: undefined as unknown,
}));

vi.mock('../api/hooks', () => ({
  keys: { update: ['update'], version: ['version'], settings: ['settings'] },
  useSettings: () => ({ data: { updateChannel: 'stable' } }),
  useUpdateSettings: () => ({ mutate: spies.patch, isPending: false }),
  useUpdateStatus: () => ({ data: spies.status }),
  useCheckUpdate: () => ({ mutate: vi.fn(), isPending: false, isError: false }),
  useApplyContainerUpdate: () => ({ mutate: spies.apply, isPending: false }),
}));

vi.mock('../shell/bridge', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  getBridge: () => spies.bridge,
}));

const t = (key: Parameters<typeof translate>[1], vars?: Record<string, string | number>) => translate('en', key, vars);

const status = (over: Partial<UpdateStatus> = {}): UpdateStatus => ({
  current: '1.0.0',
  available: '1.0.1',
  channel: 'stable',
  autoCheck: true,
  feedConfigured: true,
  ...over,
});

const show = () => {
  localStorage.setItem('astra.locale', 'en');
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={client}>
      <I18nProvider>
        <FeedbackProvider>
          <UpdateRows />
        </FeedbackProvider>
      </I18nProvider>
    </QueryClientProvider>,
  );
};

beforeEach(() => {
  spies.apply.mockReset();
  spies.patch.mockReset();
  spies.bridge = undefined;
});
afterEach(cleanup);

describe('update rows in Docker', () => {
  it('offers no install in a plain browser without a container updater', () => {
    spies.status = status();
    show();
    expect(screen.queryByRole('button', { name: t('settings.updateApply') })).toBeNull();
    expect(screen.queryByText(t('settings.autoApply'))).toBeNull();
    expect(screen.getByText(new RegExp(t('settings.updateApplyHint')))).toBeTruthy();
  });

  it('asks the container updater and toggles automatic installs', async () => {
    spies.status = status({ containerUpdater: true, autoApply: true });
    spies.apply.mockImplementation((_: unknown, o: { onSuccess: (s: UpdateStatus) => void }) =>
      o.onSuccess(status({ containerUpdater: true, applyVersion: '1.0.1', applyRequestedAt: new Date().toISOString() })),
    );
    show();
    expect(screen.getByText(new RegExp(t('settings.updateContainerHint')))).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: t('settings.updateApply') }));
    expect(spies.apply).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(screen.getByText(t('settings.updateContainerRequested'))).toBeTruthy());

    fireEvent.click(screen.getByRole('switch', { name: t('settings.autoApply') }));
    expect(spies.patch).toHaveBeenCalledWith({ updateAutoApply: false }, expect.anything());
  });

  it('shows a pending request and a failed one', () => {
    const at = new Date().toISOString();
    spies.status = status({ containerUpdater: true, applyVersion: '1.0.1', applyRequestedAt: at });
    show();
    expect(
      screen.getByText(t('settings.updateContainerPending', { version: '1.0.1', time: new Date(at).toLocaleString() })),
    ).toBeTruthy();
    cleanup();
    spies.status = status({ containerUpdater: true, applyVersion: '1.0.1', applyRequestedAt: at, applyError: 'updater-unreachable' });
    show();
    expect(screen.getByText(t('settings.updateContainerFailed', { error: 'updater-unreachable' }))).toBeTruthy();
  });
});
