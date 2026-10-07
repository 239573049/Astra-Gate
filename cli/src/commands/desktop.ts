import fs from 'node:fs';
import os from 'node:os';
import {
  applyServerUpdate,
  checkForServerUpdate,
  fetchManifest,
  installDesktopClient,
  isNewerVersion,
  majorOf,
  removeLauncher,
  type ServerControl,
  type UpdateManifest,
} from '@aidotnet/update-core';
import { info, style, success, warn } from '../ui.js';
import { astraHome, homePaths } from '../lib/paths.js';
import { npmInstallGlobal } from '../lib/desktop.js';
import { ensureDesktopServer } from '../lib/desktop-server.js';
import { platformInfo } from '../lib/platform.js';
import { apiRequest, baseUrlFor } from '../lib/http.js';
import { confirmPrompt } from '../lib/prompt.js';
import {
  applyDesktopInfo,
  clearDesktopInfo,
  readInstallJson,
  writeInstallJson,
} from '../lib/install-json.js';
import { currentRuntime, probeRunning, serverStatus, startServer, stopServer } from '../lib/server-lifecycle.js';
import { PACKAGE_NAME, VERSION } from '../version.js';

export async function runInstall(opts: { client?: boolean }): Promise<void> {
  if (!opts.client) {
    console.log('The Astra server is installed automatically with the npm package.');
    console.log(`To install the desktop app, run ${style.cyan('astra install --desktop')}.`);
    return;
  }
  const home = astraHome();
  await installDesktopAt(home, VERSION);
  // The desktop app needs a durable serverPath in install.json (it has no
  // platform-package fallback) — essential when running via npx.
  const server = ensureDesktopServer({ home, version: VERSION, platform: platformInfo().platform });
  if (server.action === 'missing') {
    warn(
      `The desktop app cannot find an Astra server: ${server.reason} ` +
        `Install the CLI (\`npm i -g ${PACKAGE_NAME}\`) and run \`astra start\` once, or set ASTRA_SERVER_BIN.`,
    );
  } else if (server.action === 'copied') {
    success(`Astra server ${VERSION} installed to ${server.serverPath}.`);
  }
  info('Launch it from your applications, or run `astra open` for the web UI.');
}

export async function runUninstallClient(): Promise<void> {
  const home = astraHome();
  const install = readInstallJson(home);
  if (!install?.desktopPath && !fs.existsSync(homePaths(home).desktopPrefix)) {
    console.log('The desktop app is not installed.');
    return;
  }
  const platform = platformInfo().platform;
  removeLauncher(platform, os.homedir());
  // Releases up to 0.2.1 put the launcher under the Astra data dir by mistake.
  removeLauncher(platform, home);
  fs.rmSync(homePaths(home).desktopPrefix, { recursive: true, force: true });
  writeInstallJson(home, clearDesktopInfo(readInstallJson(home)));
  success('Astra desktop app uninstalled.');
}

export interface UpdateArgs {
  client?: boolean;
  check?: boolean;
  retry?: boolean;
}

export async function runUpdate(opts: UpdateArgs): Promise<void> {
  const home = astraHome();

  if (opts.check) {
    await reportCheck();
    return;
  }

  // The feed manifest drives every target version; --client falls back to the
  // running CLI version when the feed is unreachable (offline reinstall).
  let manifest: UpdateManifest;
  try {
    manifest = await fetchManifest({ userAgent: `Astra-CLI/${VERSION}` });
  } catch (err) {
    if (!opts.client) throw err;
    manifest = { version: VERSION };
  }

  if (!opts.client) {
    await updateServer(home, manifest, opts.retry ?? false);

    // CLI self-update: the staged/verified flow above covers the server
    // binary; the npm global package carries the CLI code itself.
    if (isNewerVersion(manifest.version, VERSION)) {
      console.log(`Updating the Astra CLI (npm install -g ${PACKAGE_NAME}@latest)…`);
      const r = await npmInstallGlobal();
      if (r.code !== 0) {
        info(`npm install -g ${PACKAGE_NAME}@latest failed — the server is updated; run \`npm i -g ${PACKAGE_NAME}\` for the CLI.`);
      } else {
        success('The new CLI version is used the next time you run `astra`.');
      }
    }
  }

  const install = readInstallJson(home);
  const wantsDesktop = opts.client || !!install?.desktopPath || fs.existsSync(homePaths(home).desktopPrefix);
  if (wantsDesktop) {
    const target = isNewerVersion(manifest.version, VERSION) ? manifest.version : VERSION;
    await installDesktopAt(home, target);
  }
}

async function reportCheck(): Promise<void> {
  const status = await serverStatus();
  const r = await checkForServerUpdate({
    currentVersion: VERSION,
    baseUrl: status.running ? (status.base ?? undefined) : undefined,
  });
  if (r.availableVersion) {
    console.log(`Update available: ${style.cyan(r.availableVersion)} (current ${VERSION}).`);
    if (r.notes) console.log(style.dim(r.notes));
    console.log(`Run ${style.cyan('astra update')} to install.`);
    process.exitCode = 1;
  } else {
    success(`Astra is up to date (${VERSION}).`);
  }
}

/** Installs (or refreshes) the desktop app and records it in install.json. */
async function installDesktopAt(home: string, version: string): Promise<void> {
  const plat = platformInfo();
  console.log(`Installing the Astra desktop app ${version} (this may take a minute)…`);
  const entry = await installDesktopClient({
    home,
    version,
    desktopPackage: plat.desktopPackage,
    platform: plat.platform,
  });
  writeInstallJson(
    home,
    applyDesktopInfo(readInstallJson(home), {
      desktopPath: entry.path,
      desktopVersion: entry.version,
    }),
  );
  success(`Astra desktop app installed (${entry.version}).`);
}

/**
 * Server track: staged download → sha256 verify → swap → restart, with
 * automatic rollback (plan §P2). The new binary lands on a fresh versioned
 * path and install.json repoints at it, so nothing running is replaced.
 */
async function updateServer(home: string, manifest: UpdateManifest, force: boolean): Promise<void> {
  if (!isNewerVersion(manifest.version, VERSION)) {
    console.log(`Astra server is up to date (${VERSION}).`);
    return;
  }

  const plat = platformInfo();
  const status = await serverStatus();
  const ok = await confirmPrompt(
    `Update the Astra server ${VERSION} → ${manifest.version}? The gateway restarts and in-flight requests are interrupted. [y/N] `,
  );
  if (!ok) {
    console.log('Update cancelled.');
    return;
  }

  const control: ServerControl = {
    isRunning: async () => (await probeRunning(home)) !== null,
    stop: async () => {
      await stopServer();
    },
    start: async () => {
      await startServer({});
    },
    probeVersion: async () => {
      const rt = currentRuntime(home);
      if (!rt) return null;
      try {
        const v = await apiRequest<{ version?: string }>({
          base: baseUrlFor(rt.host, rt.port),
          path: '/api/version',
        });
        return v.version ?? null;
      } catch {
        return null;
      }
    },
  };

  await applyServerUpdate({
    home,
    manifest,
    platformKey: `${plat.platform}-${plat.arch}`,
    serverPackage: plat.serverPackage,
    platform: plat.platform,
    currentServerPath: readInstallJson(home)?.serverPath ?? null,
    currentVersion: VERSION,
    // The running server's API major is the compatibility anchor (fallback 1).
    expectedApiMajor: majorOf(status.runtime?.apiVersion) ?? 1,
    control,
    force,
    log: (line) => console.log(`  ${line}`),
  });
  success(`Astra server updated to ${manifest.version}.`);
}
