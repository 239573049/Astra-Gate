import * as path from 'node:path';

export type ResolvedRendererFile =
  | { kind: 'file' | 'spa'; absPath: string; contentType: string }
  | { kind: 'forbidden' }
  | { kind: 'notFound' };

export const INDEX_HTML = 'index.html';

const MIME: Record<string, string> = {
  html: 'text/html',
  js: 'text/javascript',
  mjs: 'text/javascript',
  css: 'text/css',
  json: 'application/json',
  map: 'application/json',
  txt: 'text/plain',
  xml: 'application/xml',
  svg: 'image/svg+xml',
  png: 'image/png',
  jpg: 'image/jpeg',
  jpeg: 'image/jpeg',
  gif: 'image/gif',
  webp: 'image/webp',
  avif: 'image/avif',
  ico: 'image/x-icon',
  woff: 'font/woff',
  woff2: 'font/woff2',
  ttf: 'font/ttf',
  otf: 'font/otf',
  eot: 'application/vnd.ms-fontobject',
  wasm: 'application/wasm',
  mp4: 'video/mp4',
  webm: 'video/webm',
  webmanifest: 'application/manifest+json',
};

const TEXT_UTF8 = new Set(['html', 'js', 'mjs', 'css', 'json', 'map', 'txt', 'xml', 'svg', 'webmanifest']);

export function contentTypeFor(filePath: string): string {
  const ext = path.extname(filePath).replace(/^\./, '').toLowerCase();
  const base = MIME[ext] ?? 'application/octet-stream';
  return TEXT_UTF8.has(ext) ? `${base}; charset=utf-8` : base;
}

/**
 * Resolves an app://astra URL pathname to a file under `root`.
 *
 * Security: the decoded path is POSIX-normalized, `..` segments are rejected,
 * and the absolute result must stay inside `root` (belt and braces for
 * platform-specific separators). Requests without a file extension that miss
 * on disk fall back to index.html (SPA routing); missing assets with an
 * extension are 404s.
 */
export function resolveRendererPath(
  root: string,
  rawPath: string,
  exists: (p: string) => boolean,
): ResolvedRendererFile {
  if (rawPath.includes('\0')) return { kind: 'forbidden' };
  let decoded: string;
  try {
    decoded = decodeURIComponent(rawPath);
  } catch {
    return { kind: 'forbidden' };
  }
  if (decoded.includes('\0')) return { kind: 'forbidden' };
  if (!decoded.startsWith('/')) return { kind: 'notFound' };

  const normalized = path.posix.normalize(decoded);
  if (normalized.split('/').includes('..')) return { kind: 'forbidden' };

  const indexAbs = path.join(root, INDEX_HTML);
  const relFile = normalized === '/' ? INDEX_HTML : normalized.replace(/^\//, '');
  const abs = path.resolve(root, relFile);
  if (abs !== root && !abs.startsWith(root + path.sep)) return { kind: 'forbidden' };

  if (exists(abs)) {
    return { kind: 'file', absPath: abs, contentType: contentTypeFor(abs) };
  }
  if (relFile === INDEX_HTML || path.extname(relFile) === '') {
    if (exists(indexAbs)) {
      return { kind: 'spa', absPath: indexAbs, contentType: contentTypeFor(indexAbs) };
    }
    return { kind: 'notFound' };
  }
  return { kind: 'notFound' };
}
