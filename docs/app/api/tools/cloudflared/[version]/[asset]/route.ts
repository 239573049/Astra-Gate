import { NextResponse } from 'next/server';

import { CLOUDFLARED_ASSETS, isCloudflaredVersion, openCloudflared } from '@/lib/tools-mirror';

/**
 * cloudflared download mirror for Astra's remote access (lib/tools-mirror.ts):
 *
 *   GET /api/tools/cloudflared/<version>/<asset>
 *
 * Same file names as cloudflared's GitHub releases, so Astra's client builds the URL the same way for either source.
 * A file not cached yet streams through from GitHub while a verified copy is kept; later requests are served from disk.
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';
// The first request for a file streams the GitHub download through (tens of MB).
export const maxDuration = 600;

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ version: string; asset: string }> },
): Promise<Response> {
  const { version, asset } = await ctx.params;
  if (!isCloudflaredVersion(version) || !CLOUDFLARED_ASSETS.has(asset)) {
    return NextResponse.json({ error: 'Not found' }, { status: 404 });
  }
  const file = await openCloudflared(version, asset);
  if (!file) return NextResponse.json({ error: 'Not available from the mirror' }, { status: 404 });
  return new Response(file.body, {
    headers: {
      'content-type': asset.endsWith('.tgz') ? 'application/gzip' : 'application/octet-stream',
      ...(file.size !== null && { 'content-length': String(file.size) }),
      'content-disposition': `attachment; filename="${asset}"`,
      // A release's files never change.
      'cache-control': 'public, max-age=31536000, immutable',
    },
  });
}
