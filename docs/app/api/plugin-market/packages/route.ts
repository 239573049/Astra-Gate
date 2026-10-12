import fsp from 'node:fs/promises';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { NextResponse } from 'next/server';

import { cleanMeta, metaFromPackageJson } from '@/lib/plugin-market/meta';
import {
  MAX_TARBALL_BYTES,
  emptyEntry,
  isPackageName,
  isSemver,
  marketRoot,
  packageDirName,
  packageFile,
  packageUrl,
  updateEntries,
  verifyPublishToken,
} from '@/lib/plugin-market/store';
import { TarballError, readPackageJson, receiveTarball } from '@/lib/plugin-market/tarball';

/**
 * Publish a plugin package (an `npm pack` tarball) to the market.
 *
 *   POST /api/plugin-market/packages
 *   Authorization: Bearer $PLUGIN_UPLOAD_TOKEN
 *   x-astra-package: @scope/name       (optional; must equal the name inside the tarball)
 *   x-astra-meta-b64: <base64 JSON>    (optional; name, icon, kind, providers, replaces, summary,
 *                                       author, homepage, repository, tags, minAstraVersion)
 *   Content-Type: application/octet-stream
 *   <raw .tgz bytes>
 *
 * A version is immutable: publishing one that exists answers 409.
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

const MAX_META_B64 = 64 * 1024;

function fail(error: string, status: number): NextResponse {
  return NextResponse.json({ error }, { status });
}

export async function POST(req: Request): Promise<NextResponse> {
  const auth = verifyPublishToken(req);
  if (!auth.ok) return fail(auth.error, auth.status);
  if (!req.body) return fail('Empty request body.', 400);
  if (Number(req.headers.get('content-length') ?? 0) > MAX_TARBALL_BYTES) {
    return fail(`The tarball is larger than ${MAX_TARBALL_BYTES} bytes.`, 413);
  }

  let uploadMeta = {};
  const metaB64 = req.headers.get('x-astra-meta-b64');
  if (metaB64) {
    if (metaB64.length > MAX_META_B64) return fail('x-astra-meta-b64 is too large.', 400);
    try {
      uploadMeta = cleanMeta(JSON.parse(Buffer.from(metaB64, 'base64').toString('utf8')));
    } catch {
      return fail('x-astra-meta-b64 is not base64 JSON.', 400);
    }
  }

  const tmpDir = path.join(marketRoot(), 'tmp');
  await fsp.mkdir(tmpDir, { recursive: true });
  const tmp = path.join(tmpDir, `${randomUUID()}.tgz`);
  try {
    const saved = await receiveTarball(req.body, tmp);
    const pkg = await readPackageJson(tmp);
    const name = typeof pkg.name === 'string' ? pkg.name : '';
    const version = typeof pkg.version === 'string' ? pkg.version : '';
    if (!isPackageName(name)) return fail(`"${name}" isn't a valid npm package name.`, 400);
    if (!isSemver(version)) return fail(`"${version}" isn't a valid semver version.`, 400);
    const expected = req.headers.get('x-astra-package')?.trim();
    if (expected && expected !== name) return fail(`x-astra-package is ${expected}, but the tarball is ${name}.`, 400);

    const dir = packageDirName(name);
    const target = packageFile(dir, version);
    const now = new Date().toISOString();
    const result = await updateEntries(async (file) => {
      const entry = file.entries[name] ?? emptyEntry(name, now);
      const exists = entry.versions.some((v) => v.version === version) || (await fsp.stat(target).then(() => true, () => false));
      if (exists) return null;
      await fsp.mkdir(path.dirname(target), { recursive: true });
      await fsp.rename(tmp, target);
      const { versions, package: _p, publishedAt, updatedAt: _u, ...existingMeta } = entry;
      const tarball = { version, url: packageUrl(name, version), sha256: saved.sha256, sha512: saved.sha512, size: saved.size, publishedAt: now };
      file.entries[name] = {
        ...existingMeta,
        ...metaFromPackageJson(pkg),
        ...uploadMeta,
        package: name,
        publishedAt,
        updatedAt: now,
        versions: [...versions, tarball],
      };
      return tarball;
    });
    if (!result) return fail(`${name}@${version} is already published; versions can't be replaced. Publish a new version.`, 409);
    return NextResponse.json({ ok: true, package: name, ...result });
  } catch (err) {
    if (err instanceof TarballError) return fail(err.message, err.status);
    return fail(`Publishing failed: ${err instanceof Error ? err.message : String(err)}`, 500);
  } finally {
    await fsp.rm(tmp, { force: true });
  }
}
