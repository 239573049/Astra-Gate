import { NextResponse } from 'next/server';

import { cleanMeta } from '@/lib/plugin-market/meta';
import {
  emptyEntry,
  isPackageName,
  isSemver,
  packageDirName,
  removePackageDir,
  removePackageFile,
  updateEntries,
  verifyPublishToken,
} from '@/lib/plugin-market/store';

/**
 * Entries of the market that aren't a tarball upload, and taking entries down.
 *
 *   POST   /api/plugin-market/entries                       (Bearer $PLUGIN_UPLOAD_TOKEN)
 *          { "package": "@scope/name", name?, icon?, kind?, providers?, replaces?, summary?, spec?,
 *            description?, license?, author?, homepage?, repository?, tags?, minAstraVersion? }
 *          Lists (or re-describes) a package that Astra installs from npm — or from `spec`, github:owner/repo.
 *          An entry with the name of a community one replaces its text; hosted versions are kept.
 *   DELETE /api/plugin-market/entries?package=<name>[&version=<v>]   (Bearer)
 *          Takes the entry down — or one hosted version of it. Community entries can't be removed here.
 */
export const dynamic = 'force-dynamic';
export const runtime = 'nodejs';

const MAX_BODY = 64 * 1024;

function fail(error: string, status: number): NextResponse {
  return NextResponse.json({ error }, { status });
}

export async function POST(req: Request): Promise<NextResponse> {
  const auth = verifyPublishToken(req);
  if (!auth.ok) return fail(auth.error, auth.status);
  const raw = await req.text();
  if (raw.length > MAX_BODY) return fail('The body is too large.', 413);
  let body: unknown;
  try {
    body = JSON.parse(raw);
  } catch {
    return fail('The body is not JSON.', 400);
  }
  const pkg = body && typeof body === 'object' ? (body as { package?: unknown }).package : undefined;
  if (typeof pkg !== 'string' || !isPackageName(pkg)) return fail('"package" must be a valid npm package name.', 400);
  const meta = cleanMeta(body);
  const now = new Date().toISOString();
  const entry = await updateEntries((file) => {
    const existing = file.entries[pkg] ?? emptyEntry(pkg, now);
    file.entries[pkg] = { ...existing, ...meta, package: pkg, updatedAt: now };
    return file.entries[pkg];
  });
  return NextResponse.json({ ok: true, entry });
}

export async function DELETE(req: Request): Promise<NextResponse> {
  const auth = verifyPublishToken(req);
  if (!auth.ok) return fail(auth.error, auth.status);
  const params = new URL(req.url).searchParams;
  const pkg = params.get('package')?.trim() ?? '';
  const version = params.get('version')?.trim();
  if (!isPackageName(pkg)) return fail('"package" must be a valid npm package name.', 400);
  if (version !== undefined && !isSemver(version)) return fail('"version" must be a semver version.', 400);

  const dir = packageDirName(pkg);
  const removed = await updateEntries(async (file) => {
    const entry = file.entries[pkg];
    if (!entry) return false;
    if (version === undefined) {
      delete file.entries[pkg];
      await removePackageDir(dir);
      return true;
    }
    if (!entry.versions.some((v) => v.version === version)) return false;
    entry.versions = entry.versions.filter((v) => v.version !== version);
    entry.updatedAt = new Date().toISOString();
    await removePackageFile(dir, version);
    return true;
  });
  return removed ? NextResponse.json({ ok: true }) : fail(`No published ${version ? `${pkg}@${version}` : pkg}.`, 404);
}
