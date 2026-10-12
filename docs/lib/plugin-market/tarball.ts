import { createHash } from 'node:crypto';
import fs from 'node:fs';
import fsp from 'node:fs/promises';
import { Readable } from 'node:stream';
import { pipeline } from 'node:stream/promises';
import { createGunzip } from 'node:zlib';

import { MAX_TARBALL_BYTES } from './store';

/** Total decompressed bytes read while looking for package.json (same ceiling as the client's unpack limit). */
const MAX_UNPACKED_SCAN = 200 * 1024 * 1024;
const MAX_PACKAGE_JSON = 1024 * 1024;

export class TarballError extends Error {
  constructor(message: string, readonly status = 400) {
    super(message);
  }
}

export interface SavedUpload {
  size: number;
  sha256: string;
  /** base64, the shape npm's SRI strings and the client's Verify() use */
  sha512: string;
}

/** Streams the request body to `target`, hashing as it goes and refusing anything over the size limit. */
export async function receiveTarball(body: ReadableStream<Uint8Array>, target: string): Promise<SavedUpload> {
  const sha256 = createHash('sha256');
  const sha512 = createHash('sha512');
  let size = 0;
  const source = Readable.fromWeb(body as Parameters<typeof Readable.fromWeb>[0]);
  try {
    await pipeline(
      source,
      async function* (chunks) {
        for await (const chunk of chunks) {
          const buf = chunk as Buffer;
          size += buf.length;
          if (size > MAX_TARBALL_BYTES) throw new TarballError(`The tarball is larger than ${MAX_TARBALL_BYTES} bytes.`, 413);
          sha256.update(buf);
          sha512.update(buf);
          yield buf;
        }
      },
      fs.createWriteStream(target),
    );
  } catch (err) {
    await fsp.rm(target, { force: true });
    throw err;
  }
  if (size === 0) {
    await fsp.rm(target, { force: true });
    throw new TarballError('Empty request body.');
  }
  return { size, sha256: sha256.digest('hex'), sha512: sha512.digest('base64') };
}

function octal(field: Buffer): number {
  const text = field.toString('latin1').replace(/\0.*$/s, '').trim();
  return text.length === 0 ? 0 : parseInt(text, 8);
}

function cString(field: Buffer): string {
  const end = field.indexOf(0);
  return field.subarray(0, end < 0 ? field.length : end).toString('utf8');
}

/**
 * Reads `<top folder>/package.json` out of an npm tarball without unpacking anything to disk. Nothing but that
 * one file is ever read; the scan is capped so a gzip bomb can't tie the server up.
 */
export async function readPackageJson(file: string): Promise<Record<string, unknown>> {
  const gunzip = createGunzip();
  const input = fs.createReadStream(file).pipe(gunzip);
  let pending: Buffer = Buffer.alloc(0);
  let scanned = 0;
  // what the parser is doing: a header, bytes to skip, or bytes of package.json to collect
  let skip = 0;
  let collect: { remaining: number; parts: Buffer[] } | null = null;
  let found: Buffer | null = null;
  try {
    outer: for await (const chunk of input as AsyncIterable<Buffer>) {
      scanned += chunk.length;
      if (scanned > MAX_UNPACKED_SCAN) throw new TarballError('The package is too large unpacked.', 413);
      pending = pending.length === 0 ? chunk : Buffer.concat([pending, chunk]);
      for (;;) {
        if (skip > 0) {
          const n = Math.min(skip, pending.length);
          pending = pending.subarray(n);
          skip -= n;
          if (skip > 0) break;
        }
        if (collect) {
          const n = Math.min(collect.remaining, pending.length);
          collect.parts.push(pending.subarray(0, n));
          pending = pending.subarray(n);
          collect.remaining -= n;
          if (collect.remaining > 0) break;
          found = Buffer.concat(collect.parts);
          break outer;
        }
        if (pending.length < 512) break;
        const header = pending.subarray(0, 512);
        pending = pending.subarray(512);
        if (header.every((b) => b === 0)) continue; // end-of-archive blocks
        const type = String.fromCharCode(header[156] ?? 0x30);
        const size = octal(header.subarray(124, 136));
        const padded = Math.ceil(size / 512) * 512;
        const prefix = cString(header.subarray(345, 500));
        const name = (prefix ? `${prefix}/` : '') + cString(header.subarray(0, 100));
        const isFile = type === '0' || type === '\0';
        if (isFile && /^[^/]+\/package\.json$/.test(name.replace(/\\/g, '/'))) {
          if (size > MAX_PACKAGE_JSON) throw new TarballError('package.json is too large.');
          collect = { remaining: size, parts: [] };
        } else {
          skip = padded;
        }
      }
    }
  } catch (err) {
    if (err instanceof TarballError) throw err;
    throw new TarballError('The file is not a valid npm tarball (.tgz).');
  } finally {
    input.destroy();
    gunzip.destroy();
  }
  if (!found) throw new TarballError('The tarball has no package/package.json.');
  try {
    const parsed: unknown = JSON.parse(found.toString('utf8'));
    if (parsed && typeof parsed === 'object' && !Array.isArray(parsed)) return parsed as Record<string, unknown>;
  } catch {
    /* fall through */
  }
  throw new TarballError('package.json in the tarball is not valid JSON.');
}
