import { createReadStream } from 'node:fs';
import { stat } from 'node:fs/promises';
import path from 'node:path';
import { Readable } from 'node:stream';
import { NextResponse } from 'next/server';

import { latestWith, normalizeChannel, readFeed, releasesRoot, sanitizeToken, type FeedVersion } from '@/lib/releases';

/**
 * Public Astra Pet release feed, served from `${DATA_DIR}/pet-releases/<channel>/` (channel: stable | preview;
 * preview also offers every stable release, like the desktop client's feed):
 *
 *   GET /api/pet-releases/<channel>/latest.json[?platform=darwin-arm64]
 *        the update check: newest release (that has a file for `platform`, when given) with its files
 *   GET /api/pet-releases/<channel>/releases.json        every release, newest first
 *   GET /api/pet-releases/<channel>/v/<version>/<file>   explicit file download
 *   GET /api/pet-releases/<channel>/<file>               a release's file by name
 *
 * File urls in the JSON are relative to the site origin, so they work behind any reverse proxy.
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

async function fileResponse(filePath: string): Promise<Response> {
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
      'content-type': 'application/octet-stream',
      'content-length': String(size),
      'content-disposition': `attachment; filename="${path.basename(filePath)}"`,
      'cache-control': 'no-store',
    },
  });
}

function describe(channel: string, v: FeedVersion) {
  return {
    version: v.version,
    channel: v.channel,
    releasedAt: v.releasedAt,
    notes: v.notes ?? null,
    files: v.files.map((f) => ({
      platform: f.platform,
      kind: f.kind,
      name: f.name,
      size: f.size,
      sha256: f.sha256,
      url: `/api/pet-releases/${channel}/v/${v.version}/${f.name}`,
    })),
  };
}

export async function GET(
  req: Request,
  ctx: { params: Promise<{ channel: string; path: string[] }> },
): Promise<Response> {
  const { channel: rawChannel, path: segments } = await ctx.params;
  const channel = normalizeChannel(rawChannel);
  if (!channel) return NextResponse.json({ error: 'Unknown channel (use stable or preview).' }, { status: 404 });
  const feed = await readFeed(channel, 'pet');

  if (segments.length === 1 && segments[0] === 'releases.json') {
    return NextResponse.json({ channel, latest: latestWith(feed)?.version ?? null, versions: feed.map((v) => describe(channel, v)) });
  }

  if (segments.length === 1 && segments[0] === 'latest.json') {
    // A release is uploaded file by file (one job per platform), so "newest" can briefly lack a platform's file.
    // With ?platform= the update check gets the newest release that actually has a file for that platform.
    const platform = sanitizeToken(new URL(req.url).searchParams.get('platform') ?? '', 64);
    const candidates = platform ? feed.filter((v) => v.files.some((f) => f.platform === platform)) : feed;
    const latest = latestWith(candidates);
    if (!latest) return NextResponse.json({ error: 'No release published yet.' }, { status: 404 });
    return NextResponse.json({ product: 'astra-pet', page: '/pet', ...describe(channel, latest) });
  }

  // Explicit version file: /v/<version>/<filename>
  if (segments.length === 3 && segments[0] === 'v') {
    const version = sanitizeToken(segments[1]!, 32);
    const name = sanitizeToken(segments[2]!, 128);
    const entry = feed.find((v) => v.version === version);
    if (!entry || !entry.files.some((f) => f.name === name)) return NextResponse.json({ error: 'Not found' }, { status: 404 });
    return fileResponse(path.join(releasesRoot(entry.channel, 'pet'), version, name));
  }

  // Bare filename: file names carry the version, so look it up across the feed.
  if (segments.length === 1) {
    const name = sanitizeToken(segments[0]!, 128);
    const entry = feed.find((v) => v.files.some((f) => f.name === name));
    if (entry) return fileResponse(path.join(releasesRoot(entry.channel, 'pet'), entry.version, name));
  }

  return NextResponse.json({ error: 'Not found' }, { status: 404 });
}
