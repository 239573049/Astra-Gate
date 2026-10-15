import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import path from 'node:path';
import { Readable } from 'node:stream';
import { NextResponse } from 'next/server';

import { latestWith, normalizeChannel, readFeed, releasesRoot, renderLatestMacYml, sanitizeToken } from '@/lib/releases';

/**
 * Public client-release feed, served from disk under
 * `${DATA_DIR}/client-releases/<channel>/` (channel: stable | preview; preview
 * also offers every stable release, see FEED_SOURCES in lib/releases.ts):
 *
 *   GET /api/client-releases/<channel>/latest.json       server-update manifest of the newest release that has one
 *   GET /api/client-releases/<channel>/latest-mac.yml    electron-updater (macOS) feed
 *   GET /api/client-releases/<channel>/releases.json     machine-readable index summary
 *   GET /api/client-releases/<channel>/v/<version>/<file>  explicit file download
 *   GET /api/client-releases/<channel>/<file>            a release's file by name
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
  const channel = normalizeChannel(rawChannel);
  if (!channel) return NextResponse.json({ error: 'Unknown channel (use stable or preview).' }, { status: 404 });
  const feed = await readFeed(channel);
  const latest = latestWith(feed);

  if (segments.length === 1 && segments[0] === 'releases.json') {
    return NextResponse.json({
      channel,
      latest: latest?.version ?? null,
      releasedAt: latest?.releasedAt ?? null,
      notes: latest?.notes ?? null,
      versions: feed.map((v) => ({
        version: v.version,
        channel: v.channel,
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
    const mac = latestWith(feed, 'zip');
    if (!mac) return NextResponse.json({ error: 'No macOS zip artifacts published yet.' }, { status: 404 });
    const yml = renderLatestMacYml(mac);
    if (!yml) return NextResponse.json({ error: 'No macOS zip artifacts in the latest release.' }, { status: 404 });
    return new Response(yml, {
      headers: { 'content-type': 'text/yaml; charset=utf-8', 'cache-control': 'no-store' },
    });
  }

  if (segments.length === 1 && segments[0] === 'latest.json') {
    // Server-update manifest (update-core / UpdateCheckService contract),
    // uploaded by the release workflow with kind=json / platform=manifest once
    // npm serves the packages it names — a release whose installers are up but
    // whose manifest is not yet is skipped, not a 404.
    const release = latestWith(feed, 'json');
    if (!release) return NextResponse.json({ error: 'No server-update manifest published yet.' }, { status: 404 });
    const manifest = release.files.find((f) => f.kind === 'json')!;
    return fileResponse(path.join(releasesRoot(release.channel), release.version, manifest.name), 'application/json');
  }

  // Explicit version file: /v/<version>/<filename>
  if (segments.length === 3 && segments[0] === 'v') {
    const version = sanitizeToken(segments[1]!, 32);
    const name = sanitizeToken(segments[2]!, 128);
    const entry = feed.find((v) => v.version === version);
    if (!entry || !entry.files.some((f) => f.name === name)) {
      return NextResponse.json({ error: 'Not found' }, { status: 404 });
    }
    return fileResponse(path.join(releasesRoot(entry.channel), version, name));
  }

  // Bare filename (electron-updater feed): file names carry the version, so look it up across the feed.
  if (segments.length === 1) {
    const name = sanitizeToken(segments[0]!, 128);
    const entry = feed.find((v) => v.files.some((f) => f.name === name));
    if (entry) return fileResponse(path.join(releasesRoot(entry.channel), entry.version, name));
  }

  return NextResponse.json({ error: 'Not found' }, { status: 404 });
}
