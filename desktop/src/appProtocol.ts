import * as fs from 'node:fs';
import * as fsp from 'node:fs/promises';

import { buildCsp } from './shared/csp';
import { resolveRendererPath } from './shared/rendererPath';

const APP_HOST = 'astra';

function textResponse(status: number, message: string): Response {
  return new Response(message, {
    status,
    headers: { 'content-type': 'text/plain; charset=utf-8' },
  });
}

/**
 * Serves the bundled renderer over app://astra with SPA fallback and a
 * strict CSP whose connect-src includes the current apiBase origin.
 * Works both from the app directory and from inside app.asar (fs is
 * Electron-patched for asar paths).
 */
export async function handleAppRequest(
  request: Request,
  root: string,
  apiOrigin: string,
): Promise<Response> {
  let url: URL;
  try {
    url = new URL(request.url);
  } catch {
    return textResponse(400, 'Bad request');
  }
  if (url.protocol !== 'app:' || url.hostname.toLowerCase() !== APP_HOST) {
    return textResponse(404, 'Not found');
  }

  const resolved = resolveRendererPath(root, url.pathname, (p) => {
    try {
      return fs.statSync(p).isFile();
    } catch {
      return false;
    }
  });
  if (resolved.kind === 'forbidden') return textResponse(403, 'Forbidden');
  if (resolved.kind === 'notFound') return textResponse(404, 'Not found');

  try {
    const data = await fsp.readFile(resolved.absPath);
    const headers = new Headers({
      'content-type': resolved.contentType,
      'content-security-policy': buildCsp(apiOrigin),
      'x-content-type-options': 'nosniff',
    });
    if (resolved.absPath.endsWith('.html')) headers.set('cache-control', 'no-cache');
    return new Response(data, { status: 200, headers });
  } catch {
    return textResponse(500, 'Failed to read file');
  }
}
