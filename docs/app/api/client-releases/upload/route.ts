import { NextResponse } from 'next/server';

import { channelOfVersion, isValidVersion, normalizeChannel, sanitizeToken, saveUpload, verifyUploadToken, DEFAULT_CHANNEL } from '@/lib/releases';

/**
 * Upload a desktop release artifact.
 *
 *   POST /api/client-releases/upload
 *   Authorization: Bearer $CLIENT_UPLOAD_TOKEN
 *   x-astra-version: 0.2.0        (required, x.y.z or x.y.z-preview.N)
 *   x-astra-channel: stable|preview (optional, default stable; must match the version)
 *   x-astra-platform: darwin-arm64 (required)
 *   x-astra-kind: dmg|zip|…        (required, file extension)
 *   x-astra-notes-b64: <base64>    (optional, release notes for the version)
 *   Content-Type: application/octet-stream
 *   <raw file bytes>
 *
 * Files are streamed to `${DATA_DIR}/client-releases/<channel>/<version>/`.
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function POST(req: Request): Promise<NextResponse> {
  const auth = verifyUploadToken(req);
  if (!auth.ok) return NextResponse.json({ error: auth.error }, { status: auth.status });

  const version = sanitizeToken(req.headers.get('x-astra-version') ?? '', 32);
  const platform = sanitizeToken(req.headers.get('x-astra-platform') ?? '', 64);
  const kind = sanitizeToken(req.headers.get('x-astra-kind') ?? '', 16);
  const rawChannel = req.headers.get('x-astra-channel');
  const channel = rawChannel ? normalizeChannel(rawChannel) : DEFAULT_CHANNEL;
  const notesB64 = req.headers.get('x-astra-notes-b64');

  if (!isValidVersion(version)) {
    return NextResponse.json({ error: 'Missing or invalid x-astra-version header (expected semver).' }, { status: 400 });
  }
  if (!channel) return NextResponse.json({ error: 'Unknown x-astra-channel (use stable or preview).' }, { status: 400 });
  // The channel follows from the version: a preview must never land in stable, nor a stable in preview.
  if (channelOfVersion(version) !== channel) {
    return NextResponse.json(
      { error: `Version ${version} does not belong in the ${channel} channel (x.y.z is stable, x.y.z-preview.N is preview).` },
      { status: 400 },
    );
  }
  if (!platform) return NextResponse.json({ error: 'Missing x-astra-platform header.' }, { status: 400 });
  if (!kind) return NextResponse.json({ error: 'Missing x-astra-kind header.' }, { status: 400 });
  if (!req.body) return NextResponse.json({ error: 'Empty request body.' }, { status: 400 });

  try {
    const { file, version: entry } = await saveUpload({
      channel,
      version,
      platform,
      kind,
      notesB64: notesB64 && notesB64.length > 0 ? notesB64 : null,
      body: req.body,
    });
    return NextResponse.json({
      ok: true,
      channel,
      version: entry.version,
      file: {
        ...file,
        url: `/api/client-releases/${channel}/v/${entry.version}/${file.name}`,
      },
    });
  } catch (err) {
    return NextResponse.json(
      { error: `Upload failed: ${err instanceof Error ? err.message : String(err)}` },
      { status: 500 },
    );
  }
}
