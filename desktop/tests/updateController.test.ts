import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const electron = vi.hoisted(() => ({
  showMessageBox: vi.fn(),
  notifications: [] as { title: string; body: string }[],
}));

vi.mock('electron', () => ({
  app: { getVersion: () => '0.4.0', getPath: () => '/tmp', isPackaged: true },
  dialog: { showMessageBox: electron.showMessageBox },
  Notification: class {
    static isSupported(): boolean {
      return true;
    }
    constructor(private readonly o: { title: string; body: string }) {}
    on(): void {}
    show(): void {
      electron.notifications.push(this.o);
    }
  },
}));
vi.mock('electron-updater', () => ({ autoUpdater: { on: vi.fn(), checkForUpdates: vi.fn() } }));

import { REMINDER_COOLDOWN_MS, EMPTY_REMINDER_STATE, type UpdateReminderState } from '../src/shared/updateReminder';
import { UpdateController, type UpdateControllerOptions } from '../src/update';

const BASE = 'http://127.0.0.1:17321';
const FEED = 'https://astra-gate.si/api/client-releases/stable/latest.json';

interface World {
  autoCheck: boolean;
  available: string | null;
  calls: string[];
}

function stubFetch(world: World): void {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: string | URL, init?: { method?: string }) => {
      const url = String(input);
      const method = init?.method ?? 'GET';
      world.calls.push(`${method} ${url}`);
      const json = (body: unknown): Response => new Response(JSON.stringify(body), { status: 200 });
      if (url === `${BASE}/api/update/status`) {
        return json({ current: '0.4.0', available: world.available, autoCheck: world.autoCheck });
      }
      if (url === `${BASE}/api/update/check`) return json({});
      if (url === FEED) return json({ version: world.available ?? '0.4.0', apiVersion: '1.0', notes: 'release notes' });
      throw new Error(`unexpected fetch ${method} ${url}`);
    }),
  );
}

describe('launch-time update check and reminder', () => {
  let world: World;
  let store: UpdateReminderState;
  let clock: number;
  let mode: 'dialog' | 'notification';

  /** A fresh controller = a fresh app launch; the persisted reminder store survives. */
  function launch(overrides: Partial<UpdateControllerOptions> = {}): UpdateController {
    return new UpdateController({
      home: '/tmp/astra-test-home',
      installDesktopPath: null,
      expectedApiMajor: 1,
      apiBase: () => BASE,
      platform: 'linux',
      isPackaged: true,
      exePath: '/opt/Astra/astra',
      serverPackage: '@aidotnet/server-linux-x64',
      desktopPackage: '@aidotnet/desktop-linux-x64',
      control: {
        isRunning: async () => true,
        stop: async () => {},
        start: async () => {},
        probeVersion: async () => '0.4.0',
      },
      reminder: {
        load: () => store,
        save: (s) => {
          store = s;
        },
        mode: () => mode,
        locale: () => 'en-US',
        now: () => clock,
      },
      log: () => {},
      ...overrides,
    });
  }

  beforeEach(() => {
    world = { autoCheck: true, available: '0.5.0', calls: [] };
    store = { ...EMPTY_REMINDER_STATE };
    clock = 1_800_000_000_000;
    mode = 'dialog';
    electron.showMessageBox.mockReset();
    electron.showMessageBox.mockResolvedValue({ response: 1 }); // "Remind Me Later"
    electron.notifications.length = 0;
    stubFetch(world);
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('checks on launch (asking the server to poll the feed first) and reminds once', async () => {
    await launch().backgroundCheck();

    expect(world.calls).toContain(`POST ${BASE}/api/update/check`);
    expect(world.calls.indexOf(`POST ${BASE}/api/update/check`)).toBeLessThan(world.calls.indexOf(`GET ${BASE}/api/update/status`, 1));
    expect(electron.showMessageBox).toHaveBeenCalledTimes(1);
    expect(electron.showMessageBox.mock.calls[0]![0]).toMatchObject({ message: 'Astra 0.5.0 is available' });
    expect(store).toMatchObject({ version: '0.5.0', count: 1 });
  });

  it('does not remind again on the next launch within the cooldown', async () => {
    await launch().backgroundCheck();
    clock += 60 * 60 * 1000; // relaunch an hour later
    await launch().backgroundCheck();
    expect(electron.showMessageBox).toHaveBeenCalledTimes(1);
  });

  it('reminds again after the cooldown but gives up after three reminders', async () => {
    for (let i = 0; i < 6; i++) {
      await launch().backgroundCheck();
      clock += REMINDER_COOLDOWN_MS + 1000;
    }
    expect(electron.showMessageBox).toHaveBeenCalledTimes(3);
    expect(store.count).toBe(3);
  });

  it('"Skip This Version" silences that version for good but not the next release', async () => {
    electron.showMessageBox.mockResolvedValue({ response: 2 });
    await launch().backgroundCheck();
    expect(store.skippedVersion).toBe('0.5.0');

    clock += 30 * REMINDER_COOLDOWN_MS;
    await launch().backgroundCheck();
    expect(electron.showMessageBox).toHaveBeenCalledTimes(1);

    world.available = '0.6.0';
    await launch().backgroundCheck();
    expect(electron.showMessageBox).toHaveBeenCalledTimes(2);
    expect(electron.showMessageBox.mock.calls[1]![0]).toMatchObject({ message: 'Astra 0.6.0 is available' });
  });

  it('says nothing when the install is up to date', async () => {
    world.available = null;
    await launch().backgroundCheck();
    expect(electron.showMessageBox).not.toHaveBeenCalled();
    expect(store).toEqual(EMPTY_REMINDER_STATE);
  });

  it('does not even check when automatic update checks are turned off', async () => {
    world.autoCheck = false;
    await launch().backgroundCheck();
    expect(world.calls).toEqual([`GET ${BASE}/api/update/status`]);
    expect(electron.showMessageBox).not.toHaveBeenCalled();
  });

  it('uses a quiet notification instead of a dialog when no window is showing, and still counts it', async () => {
    mode = 'notification';
    await launch().backgroundCheck();
    expect(electron.showMessageBox).not.toHaveBeenCalled();
    expect(electron.notifications).toEqual([{ title: 'Astra 0.5.0 is available', body: 'Click to review and update' }]);
    expect(store.count).toBe(1);
  });

  it('manual checks never remind and never consume the budget', async () => {
    const controller = launch();
    await controller.check();
    expect(electron.showMessageBox).not.toHaveBeenCalled();
    expect(store).toEqual(EMPTY_REMINDER_STATE);
    expect(controller.hasUpdate).toBe(true);
  });

  it('without a reminder option the launch check only flags the tray', async () => {
    const controller = launch({ reminder: undefined });
    await controller.backgroundCheck();
    expect(electron.showMessageBox).not.toHaveBeenCalled();
    expect(controller.hasUpdate).toBe(true);
  });
});
