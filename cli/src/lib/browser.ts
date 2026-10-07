import { spawn } from 'node:child_process';

export function openInBrowser(url: string, platform: string = process.platform): void {
  try {
    if (platform === 'darwin') {
      spawn('open', [url], { detached: true, stdio: 'ignore' }).unref();
      return;
    }
    if (platform === 'win32') {
      spawn('rundll32', ['url.dll,FileProtocolHandler', url], {
        detached: true,
        stdio: 'ignore',
        windowsHide: true,
      }).unref();
      return;
    }
    spawn('xdg-open', [url], { detached: true, stdio: 'ignore' }).unref();
  } catch {
    // Nothing worked — the URL is printed anyway by the caller.
  }
}
