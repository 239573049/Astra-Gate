import { NextResponse } from 'next/server';

import { searchPlugins } from '@/lib/plugin-market/search';

/**
 * Search for the Astra client: published entries first, then npm (OpenCode / magpie / pi packages) and GitHub's
 * magpie-plugin topic.
 *
 *   GET /api/plugin-market/search?q=<terms>
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function GET(req: Request): Promise<NextResponse> {
  const q = new URL(req.url).searchParams.get('q')?.trim() ?? '';
  if (q.length === 0) return NextResponse.json({ error: 'q is required' }, { status: 400 });
  if (q.length > 100) return NextResponse.json({ error: 'q is too long (100 characters at most)' }, { status: 400 });
  return NextResponse.json(await searchPlugins(q), { headers: { 'cache-control': 'public, max-age=60' } });
}
