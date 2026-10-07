import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import path from 'node:path';
import { Readable } from 'node:stream';
import { NextResponse } from 'next/server';

import { DEFAULT_CHANNEL, latestVersion, readIndex, releasesRoot, renderLatestMacYml, sanitizeToken } from '@/lib/releases';

/**
 * Public client-release feed, served from disk under
 * `${DATA_DIR}/client-releases/<channel>/`:
 *
 *   GET /api/client-releases/<channel>/latest-mac.yml    electron-updater (macOS) feed
 *   GET /api/client-releases/<channel>/releases.json     machine-readable index summary
 *   GET /api/client-releases/<channel>/v/<version>/<file>  explicit file download
 *   GET /api/client-releases/<channel>/<file>            latest version's file
 *                                                        (electron-updater resolves
 *                                                        urls relative to the feed)
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

async function fileResponse(filePath: string, contentType = 'application/octet-stream'): Promise<Response> {
  let size: number;
  try {
    const st = await stat(filePath);
    if (!st.isFile()) return NextResponse.json({ error: 'Not found' }, { status: 404 });
    size = st.size;
  } catch {
    return NextResponse.json({ error: 'Not found' }, { status: 404 });
  }
  const stream = Readable.toWeb(createReadStream(filePath)) as ReadableStream;
  return new Response(stream, {
    headers: {
      'content-type': contentType,
      'content-length': String(size),
      'cache-control': 'no-store',
    },
  });
}

export async function GET(
  _req: Request,
  ctx: { params: Promise<{ channel: string; path: string[] }> },
): Promise<Response> {
  const { channel: rawChannel, path: segments } = await ctx.params;
  const channel = sanitizeToken(rawChannel, 32) || DEFAULT_CHANNEL;
  const index = await readIndex(channel);
  const latest = latestVersion(index);

  if (segments.length === 1 && segments[0] === 'releases.json') {
    return NextResponse.json({
      channel,
      latest: latest?.version ?? null,
      releasedAt: latest?.releasedAt ?? null,
      notes: latest?.notes ?? null,
      versions: Object.values(index.versions)
        .sort((a, b) => (a.version < b.version ? 1 : -1))
        .map((v) => ({
          version: v.version,
          releasedAt: v.releasedAt,
          notes: v.notes ?? null,
          files: v.files.map((f) => ({
            ...f,
            url: `/api/client-releases/${channel}/v/${v.version}/${f.name}`,
          })),
        })),
    });
  }

  if (segments.length === 1 && segments[0] === 'latest-mac.yml') {
    if (!latest) return NextResponse.json({ error: 'No releases published yet.' }, { status: 404 });
    const yml = renderLatestMacYml(latest);
    if (!yml) return NextResponse.json({ error: 'No macOS zip artifacts in the latest release.' }, { status: 404 });
    return new Response(yml, {
      headers: { 'content-type': 'text/yaml; charset=utf-8', 'cache-control': 'no-store' },
    });
  }

  if (segments.length === 1 && segments[0] === 'latest.json') {
    // Server-update manifest (update-core / UpdateCheckService contract),
    // uploaded by the release workflow with kind=json / platform=manifest.
    if (!latest) return NextResponse.json({ error: 'No releases published yet.' }, { status: 404 });
    const manifest = latest.files.find((f) => f.kind === 'json');
    if (!manifest) return NextResponse.json({ error: 'No server-update manifest in the latest release.' }, { status: 404 });
    return fileResponse(path.join(releasesRoot(channel), latest.version, manifest.name), 'application/json');
  }

  // Explicit version file: /v/<version>/<filename>
  if (segments.length === 3 && segments[0] === 'v') {
    const version = sanitizeToken(segments[1]!, 32);
    const name = sanitizeToken(segments[2]!, 128);
    const entry = index.versions[version];
    if (!entry || !entry.files.some((f) => f.name === name)) {
      return NextResponse.json({ error: 'Not found' }, { status: 404 });
    }
    return fileResponse(path.join(releasesRoot(channel), version, name));
  }

  // Bare filename: resolve against the latest version (electron-updater feed).
  if (segments.length === 1 && latest) {
    const name = sanitizeToken(segments[0]!, 128);
    if (latest.files.some((f) => f.name === name)) {
      return fileResponse(path.join(releasesRoot(channel), latest.version, name));
    }
  }

  return NextResponse.json({ error: 'Not found' }, { status: 404 });
}
