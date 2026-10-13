import { NextResponse } from 'next/server';

import { buildRegistry } from '@/lib/plugin-market/registry';

/**
 * The plugin market's registry: the community registry's list (astragate-community/astra-gate-plugins; entries without tarballs get npm's newest versions) merged with the entries and
 * packages published here. This is what Astra's plugin market reads by default (ASTRA_PLUGIN_MARKET overrides).
 *
 *   GET /api/plugin-market/registry.json
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function GET(req: Request): Promise<Response> {
  const { doc, etag } = await buildRegistry();
  const headers = { etag, 'cache-control': 'public, max-age=300' };
  if (req.headers.get('if-none-match') === etag) return new Response(null, { status: 304, headers });
  return NextResponse.json(doc, { headers });
}
