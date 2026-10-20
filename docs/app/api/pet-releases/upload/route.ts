import { NextResponse } from 'next/server';

import { channelOfVersion, isValidVersion, normalizeChannel, sanitizeToken, saveUpload, verifyUploadToken, DEFAULT_CHANNEL } from '@/lib/releases';

/**
 * Upload an Astra Pet release artifact (called by the Astra Pet GitHub release workflow).
 *
 *   POST /api/pet-releases/upload
 *   Authorization: Bearer $PET_UPLOAD_TOKEN
 *   x-astra-version: 0.2.0          (required, x.y.z or x.y.z-preview.N)
 *   x-astra-channel: stable|preview (optional, default stable; must match the version)
 *   x-astra-platform: darwin-arm64  (required: darwin-arm64 | darwin-x64 | win32-x64 …)
 *   x-astra-kind: zip               (required, file extension)
 *   x-astra-notes-b64: <base64>     (optional, release notes for the version)
 *   Content-Type: application/octet-stream
 *   <raw file bytes>
 *
 * Same contract as /api/client-releases/upload, but a separate store (`${DATA_DIR}/pet-releases/`) and a separate
 * token, so a leaked pet token can never publish a desktop-client release. The response carries the sha256 the server
 * computed so the workflow can compare it with the local file.
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

export async function POST(req: Request): Promise<NextResponse> {
  const auth = verifyUploadToken(req, 'pet');
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
      product: 'pet',
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
        url: `/api/pet-releases/${channel}/v/${entry.version}/${file.name}`,
      },
    });
  } catch (err) {
    return NextResponse.json(
      { error: `Upload failed: ${err instanceof Error ? err.message : String(err)}` },
      { status: 500 },
    );
  }
}
