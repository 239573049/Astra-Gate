// The crypto.* and encoding.* host calls for the Node harness, on node:crypto — the same operations, defaults, encodings and
// error codes as the host's CryptoCalls.cs (src/Astra.Plugins/V2/Runtime), so a plugin computes the same bytes in a test as in Astra.
import nodeCrypto from 'node:crypto';

export const names = new Set([
  'crypto.digest', 'crypto.hmac', 'crypto.aes', 'crypto.rsa.encrypt', 'crypto.sign', 'crypto.verify', 'crypto.pbkdf2', 'crypto.hkdf',
  'encoding.utf8Encode', 'encoding.utf8Decode', 'encoding.encode', 'encoding.decode',
]);

const InputLimit = 8 * 1024 * 1024;
const bad = (message) => Object.assign(new Error(message), { code: 'invalid_argument' });
const bin = (b) => Buffer.from(b).toString('latin1');
const unbin = (s) => Buffer.from(s, 'latin1');

const decodeText = (text, from) => {
  if (from === 'hex') {
    if (text.length % 2) throw bad('hex text has an odd length');
    if (/[^0-9a-fA-F]/.test(text)) throw bad('not valid hex text');
    return Buffer.from(text, 'hex');
  }
  if (from !== 'base64' && from !== 'base64url') throw bad('encoding must be base64, base64url or hex');
  const clean = text.replace(/[\t\n\f\r =]/g, '').replace(/-/g, '+').replace(/_/g, '/');
  if (/[^A-Za-z0-9+/]/.test(clean) || clean.length % 4 === 1) throw bad('not valid base64 text');
  return Buffer.from(clean, 'base64');
};
const encodeText = (buf, to) => {
  if (to === 'hex' || to === 'base64' || to === 'base64url') return Buffer.from(buf).toString(to);
  throw bad('encoding must be base64, base64url or hex');
};

const input = (n) => {
  let b;
  if (typeof n === 'string') b = Buffer.from(n, 'utf8');
  else if (n && typeof n === 'object') {
    if (typeof n.bin === 'string') b = unbin(n.bin);
    else if (typeof n.base64 === 'string') b = decodeText(n.base64, 'base64');
    else if (typeof n.hex === 'string') b = decodeText(n.hex, 'hex');
    else if (typeof n.text === 'string') b = Buffer.from(n.text, 'utf8');
  }
  if (!b) throw bad('data must be a string, a Uint8Array or { base64 | hex | text }');
  if (b.length > InputLimit) throw bad('input is larger than 8 MiB');
  return b;
};

const out = (buf, o, fallback) => {
  const enc = o.encoding ?? fallback;
  if (enc === 'bytes') return { bin: bin(buf) };
  if (enc === 'utf8') {
    try { return new TextDecoder('utf-8', { fatal: true }).decode(buf); } catch { throw bad('the data is not valid UTF-8'); }
  }
  if (enc === 'hex' || enc === 'base64' || enc === 'base64url') return encodeText(buf, enc);
  throw bad('encoding must be hex, base64, base64url, utf8 or bytes');
};

const hashName = (a, fallback) => {
  const n = String(a ?? fallback).toLowerCase().replace('-', '');
  if (!['md5', 'sha1', 'sha256', 'sha384', 'sha512'].includes(n)) throw bad('algorithm must be md5, sha1, sha256, sha384 or sha512');
  return n;
};

const aes = (o) => {
  const mode = o.mode ?? 'cbc';
  const encrypt = (o.op ?? 'encrypt') === 'encrypt';
  const key = input(o.key);
  if (![16, 24, 32].includes(key.length)) throw bad('an AES key is 16, 24 or 32 bytes (AES-128/192/256)');
  const data = input(o.data);
  const iv = o.iv === undefined ? Buffer.alloc(0) : input(o.iv);
  const bits = key.length * 8;
  if (mode === 'cbc' || mode === 'ecb') {
    const padding = o.padding ?? 'pkcs7';
    if (padding !== 'pkcs7' && padding !== 'none') throw bad('padding must be pkcs7 or none');
    if (mode === 'cbc' && iv.length !== 16) throw bad('AES-CBC needs a 16-byte iv');
    const c = (encrypt ? nodeCrypto.createCipheriv : nodeCrypto.createDecipheriv)(`aes-${bits}-${mode}`, key, mode === 'ecb' ? null : iv);
    c.setAutoPadding(padding === 'pkcs7');
    return Buffer.concat([c.update(data), c.final()]);
  }
  if (mode === 'gcm') {
    if (iv.length !== 12) throw bad('AES-GCM needs a 12-byte iv');
    const aad = o.aad === undefined ? Buffer.alloc(0) : input(o.aad);
    if (encrypt) {
      const c = nodeCrypto.createCipheriv(`aes-${bits}-gcm`, key, iv);
      c.setAAD(aad);
      const ct = Buffer.concat([c.update(data), c.final()]);
      return Buffer.concat([ct, c.getAuthTag()]);
    }
    let body, tag;
    if (o.tag !== undefined) { tag = input(o.tag); body = data; } else {
      if (data.length < 16) throw bad('AES-GCM data must end with its 16-byte tag');
      body = data.subarray(0, -16); tag = data.subarray(-16);
    }
    if (tag.length !== 16) throw bad('an AES-GCM tag is 16 bytes');
    const d = nodeCrypto.createDecipheriv(`aes-${bits}-gcm`, key, iv);
    d.setAAD(aad); d.setAuthTag(tag);
    return Buffer.concat([d.update(body), d.final()]);
  }
  throw bad('mode must be cbc, ecb or gcm');
};

const keyText = (n) => {
  const s = typeof n === 'string' ? n : n && typeof n.text === 'string' ? n.text : '';
  if (!s.trim()) throw bad('key must be a PEM string (or base64 DER)');
  if (s.length > 20000) throw bad('the key is too large');
  return s.trim();
};
// a bare base64 DER key is read as SPKI / PKCS#8 (or the PKCS#1 / SEC1 forms): try them in turn
const keyObject = (text, priv, kind) => {
  if (text.includes('-----BEGIN')) return priv ? nodeCrypto.createPrivateKey(text) : nodeCrypto.createPublicKey(text);
  const der = decodeText(text, 'base64');
  const types = priv ? (kind === 'rsa' ? ['pkcs8', 'pkcs1'] : ['pkcs8', 'sec1']) : kind === 'rsa' ? ['spki', 'pkcs1'] : ['spki'];
  let last;
  for (const type of types) {
    try { return priv ? nodeCrypto.createPrivateKey({ key: der, format: 'der', type }) : nodeCrypto.createPublicKey({ key: der, format: 'der', type }); } catch (e) { last = e; }
  }
  throw last;
};
const rsaBits = (k) => {
  const bits = k.asymmetricKeyDetails?.modulusLength ?? 0;
  if (k.asymmetricKeyType !== 'rsa' || bits < 512 || bits > 8192) throw bad('an RSA key is 512 to 8192 bits');
  return k;
};

const rsaEncrypt = (o) => {
  const key = rsaBits(keyObject(keyText(o.key), false, 'rsa'));
  const data = input(o.data);
  const padding = o.padding ?? 'pkcs1';
  if (padding === 'pkcs1') return nodeCrypto.publicEncrypt({ key, padding: nodeCrypto.constants.RSA_PKCS1_PADDING }, data);
  if (padding === 'oaep') return nodeCrypto.publicEncrypt({ key, padding: nodeCrypto.constants.RSA_PKCS1_OAEP_PADDING, oaepHash: hashName(o.hash, 'sha1') }, data);
  throw bad('padding must be pkcs1 or oaep');
};

const signAlg = (a) => {
  const table = {
    'rsa-sha1': ['rsa', 'sha1'], 'rsa-sha256': ['rsa', 'sha256'], 'rsa-sha384': ['rsa', 'sha384'], 'rsa-sha512': ['rsa', 'sha512'],
    'rsa-pss-sha256': ['pss', 'sha256'], 'ecdsa-p256-sha256': ['p256', 'sha256'], 'ecdsa-p384-sha384': ['p384', 'sha384'],
  };
  const e = table[String(a ?? '').toLowerCase()];
  if (!e) throw bad('algorithm must be rsa-sha1|sha256|sha384|sha512, rsa-pss-sha256, ecdsa-p256-sha256 or ecdsa-p384-sha384');
  return e;
};
const signOptions = (key, kind, o) => {
  if (kind === 'rsa') return { key, padding: nodeCrypto.constants.RSA_PKCS1_PADDING };
  if (kind === 'pss') return { key, padding: nodeCrypto.constants.RSA_PKCS1_PSS_PADDING, saltLength: 32 };
  const fmt = o.format ?? 'der';
  if (fmt !== 'der' && fmt !== 'raw' && fmt !== 'p1363') throw bad('format must be der or raw');
  return { key, dsaEncoding: fmt === 'der' ? 'der' : 'ieee-p1363' };
};
const checkKey = (k, kind) => {
  if (kind === 'rsa' || kind === 'pss') rsaBits(k);
  else if (k.asymmetricKeyType !== 'ec' || k.asymmetricKeyDetails?.namedCurve !== (kind === 'p256' ? 'prime256v1' : 'secp384r1')) throw bad(`the key is not a ${kind.toUpperCase()} key`);
  return k;
};
const sign = (o) => {
  const [kind, hash] = signAlg(o.algorithm);
  const key = checkKey(keyObject(keyText(o.key), true, kind === 'p256' || kind === 'p384' ? 'ec' : 'rsa'), kind);
  return nodeCrypto.sign(hash, input(o.data), signOptions(key, kind, o));
};
const verify = (o) => {
  const [kind, hash] = signAlg(o.algorithm);
  const key = checkKey(keyObject(keyText(o.key), false, kind === 'p256' || kind === 'p384' ? 'ec' : 'rsa'), kind);
  return nodeCrypto.verify(hash, input(o.data), signOptions(key, kind, o), input(o.signature));
};

const call = (name, o) => {
  switch (name) {
    case 'crypto.digest': return out(nodeCrypto.createHash(hashName(o.alg, 'sha256')).update(input(o.data)).digest(), o, 'hex');
    case 'crypto.hmac': return out(nodeCrypto.createHmac(hashName(o.alg ?? o.algorithm, 'sha256'), input(o.key)).update(input(o.data ?? o.text)).digest(), o, 'hex');
    case 'crypto.aes': return out(aes(o), o, o.op === 'decrypt' ? 'bytes' : 'base64');
    case 'crypto.rsa.encrypt': return out(rsaEncrypt(o), o, 'base64');
    case 'crypto.sign': return out(sign(o), o, 'base64');
    case 'crypto.verify': return verify(o);
    case 'crypto.pbkdf2': {
      const iterations = o.iterations ?? 100000, length = o.length ?? 32;
      if (!(iterations >= 1 && iterations <= 1000000)) throw bad('iterations is 1 to 1,000,000');
      if (!(length >= 1 && length <= 1024)) throw bad('length is 1 to 1024 bytes');
      return out(nodeCrypto.pbkdf2Sync(input(o.password), input(o.salt), iterations, length, hashName(o.hash, 'sha256')), o, 'hex');
    }
    case 'crypto.hkdf': {
      const length = o.length ?? 32;
      if (!(length >= 1 && length <= 1024)) throw bad('length is 1 to 1024 bytes');
      return out(Buffer.from(nodeCrypto.hkdfSync(hashName(o.hash, 'sha256'), input(o.key), o.salt === undefined ? Buffer.alloc(0) : input(o.salt), o.info === undefined ? Buffer.alloc(0) : input(o.info), length)), o, 'hex');
    }
    case 'encoding.utf8Encode': return bin(Buffer.from(o.text ?? '', 'utf8'));
    case 'encoding.utf8Decode': {
      let b = unbin(o.bin ?? '');
      if (!o.ignoreBOM && b[0] === 0xef && b[1] === 0xbb && b[2] === 0xbf) b = b.subarray(3);
      try { return new TextDecoder('utf-8', { fatal: !!o.fatal, ignoreBOM: true }).decode(b); } catch { throw bad('the data is not valid UTF-8'); }
    }
    case 'encoding.encode': return encodeText(unbin(o.bin ?? ''), o.to ?? 'base64');
    case 'encoding.decode': return bin(decodeText(o.text ?? '', o.from ?? 'base64'));
  }
  throw Object.assign(new Error(`${name} is not a synchronous call`), { code: 'not_supported' });
};

/** Answers a crypto.* / encoding.* call the way the host does; errors carry code 'invalid_argument'. */
export function answer(name, args) {
  try {
    return call(name, args && typeof args === 'object' ? args : {});
  } catch (e) {
    if (e.code === 'invalid_argument' || e.code === 'not_supported') throw e;
    throw Object.assign(new Error(`the crypto operation failed: ${e.message}`), { code: 'invalid_argument' });
  }
}
