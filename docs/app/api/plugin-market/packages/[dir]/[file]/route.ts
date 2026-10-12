import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import { Readable } from 'node:stream';
import { NextResponse } from 'next/server';

import { isDirName, isSemver, packageFile } from '@/lib/plugin-market/store';

/**
 * Download a hosted plugin package.
 *
 *   GET /api/plugin-market/packages/<dirName>/<version>.tgz
 *
 * dirName is the package name with "/" written "__" ("@scope/name" -> "@scope__name"). Versions never change, so
 * the answer is cached for good. The registry lists the exact URL and the sha256 the client checks.
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ dir: string; file: string }> },
): Promise<Response> {
  const { dir: rawDir, file } = await ctx.params;
  const dir = decodeURIComponent(rawDir);
  const m = /^(.+)\.tgz$/.exec(file);
  if (!m || !isDirName(dir) || !isSemver(m[1]!)) return NextResponse.json({ error: 'Not found' }, { status: 404 });
  const target = packageFile(dir, m[1]!);
  let size: number;
  try {
    const st = await stat(target);
    if (!st.isFile()) return NextResponse.json({ error: 'Not found' }, { status: 404 });
    size = st.size;
  } catch {
    return NextResponse.json({ error: 'Not found' }, { status: 404 });
  }
  return new Response(Readable.toWeb(createReadStream(target)) as ReadableStream, {
    headers: {
      'content-type': 'application/gzip',
      'content-length': String(size),
      'cache-control': 'public, max-age=31536000, immutable',
    },
  });
}
