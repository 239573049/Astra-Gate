import { describe, expect, it } from 'vitest';

import * as path from 'node:path';

import { contentTypeFor, resolveRendererPath } from '../src/shared/rendererPath';

function fixtureRoot(): string {
  return path.resolve('/tmp/astra-renderer-fixture');
}

function existsIn(files: string[]) {
  const root = fixtureRoot();
  return (p: string) => files.includes(path.relative(root, p));
}

describe('resolveRendererPath', () => {
  const root = fixtureRoot();

  it('serves index.html for the root path (SPA)', () => {
    const r = resolveRendererPath(root, '/', existsIn(['index.html']));
    expect(r.kind === 'file' || r.kind === 'spa').toBe(true);
    if (r.kind !== 'file' && r.kind !== 'spa') return;
    expect(r.absPath).toBe(path.join(root, 'index.html'));
    expect(r.contentType).toBe('text/html; charset=utf-8');
  });

  it('serves existing files with correct mime types', () => {
    const r = resolveRendererPath(root, '/assets/index-abc.js', existsIn(['assets/index-abc.js']));
    expect(r.kind).toBe('file');
    if (r.kind !== 'file') return;
    expect(r.absPath).toBe(path.join(root, 'assets', 'index-abc.js'));
    expect(r.contentType).toBe('text/javascript; charset=utf-8');

    const css = resolveRendererPath(root, '/assets/x.css', existsIn(['assets/x.css']));
    expect(css.kind === 'file' && css.contentType).toBe('text/css; charset=utf-8');

    const png = resolveRendererPath(root, '/logo.png', existsIn(['logo.png']));
    expect(png.kind === 'file' && png.contentType).toBe('image/png');

    const woff2 = resolveRendererPath(root, '/f.woff2', existsIn(['f.woff2']));
    expect(woff2.kind === 'file' && woff2.contentType).toBe('font/woff2');
  });

  it('falls back to index.html for extension-less SPA routes', () => {
    const r = resolveRendererPath(root, '/providers', existsIn(['index.html']));
    expect(r.kind).toBe('spa');
    if (r.kind !== 'spa') return;
    expect(r.absPath).toBe(path.join(root, 'index.html'));
  });

  it('returns notFound for missing assets with an extension', () => {
    expect(resolveRendererPath(root, '/missing.js', existsIn(['index.html'])).kind).toBe('notFound');
    expect(resolveRendererPath(root, '/index.html', existsIn([])).kind).toBe('notFound');
  });

  it('keeps encoded traversal inside the root', () => {
    // %2e%2e decodes to ".."; posix normalization collapses it safely.
    const r = resolveRendererPath(root, '/%2e%2e/%2e%2e/etc/passwd', existsIn(['index.html']));
    expect(r.kind).toBe('spa');
    if (r.kind !== 'spa') return;
    expect(r.absPath.startsWith(root + path.sep)).toBe(true);
  });

  it('rejects NUL bytes and malformed escapes', () => {
    expect(resolveRendererPath(root, '/%00', existsIn(['index.html'])).kind).toBe('forbidden');
    expect(resolveRendererPath(root, '/%zz', existsIn(['index.html'])).kind).toBe('forbidden');
  });

  it('guards against path escape via containment check', () => {
    // Simulate a hostile "exists"-independent escape: a resolved path outside
    // root must be forbidden even if normalization were bypassed.
    const outside = path.resolve(root, '..', 'secret.html');
    const r = resolveRendererPath(root, '/secret.html', (p) => p === outside);
    expect(r.kind === 'forbidden' || r.kind === 'notFound' || r.kind === 'spa').toBe(true);
    if (r.kind !== 'notFound') {
      const abs = (r as { absPath?: string }).absPath;
      if (abs !== undefined) expect(abs.startsWith(root + path.sep)).toBe(true);
    }
  });
});

describe('contentTypeFor', () => {
  it('falls back to octet-stream for unknown extensions', () => {
    expect(contentTypeFor('/a.weird')).toBe('application/octet-stream');
    expect(contentTypeFor('/noext')).toBe('application/octet-stream');
  });
});
