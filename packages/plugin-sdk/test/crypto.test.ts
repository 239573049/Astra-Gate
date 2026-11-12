import nodeCrypto from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { commands, crypto, definePlugin, encoding, http } from 'astra';
import { runCommand } from '../src/testing.mjs';

// The same vectors the xunit twin (tests/Astra.Plugins.Tests/V2/PluginV2SandboxTests.cs) runs in the real Jaunt engine.
const RSA_PRIVATE = `-----BEGIN PRIVATE KEY-----
MIICdwIBADANBgkqhkiG9w0BAQEFAASCAmEwggJdAgEAAoGBAMGh0nBfQtRjyDRq
vTXYWVqe8OvKDFzO6LFEDw/1+UCHbTX50zXyXFxGcubRlfc51Unuk3qUPpxKLbF2
89XjNtlz3I60mAH6k2Ni9PP7gk0VKE2qM3IXOypLggUaziW5rCwRXcQiOdLn4DzP
xFWdZMsGN2gWvKEbOqVr0A8Hw6/TAgMBAAECgYA0lQI1m9jlHleuzFGnfqH9Nu5L
yamDxnhwMwA4IF7YhC5HGxmdK7yjHkpznfSwl1bxZcVM41cBUC+KYrginDo3Z3KX
zoxIeT4oN87lPd+zbCf2kyRJWGUYVRw77n0q+OPK8UAOaOYaG5OyjZLqdnIR3Enu
0vYcMm7duU88bRnOCQJBAPZFTfjArWJUlRl8Pu0IfWc5IBXfFPdo0uvRMabuIaS2
QWhYd63kkQhoZEpu27J3u8awrnFAeGDq0vOsdlt+jA0CQQDJSCYtk+GnyS10erhp
VC1hu/zX6OBM99TCk120njcAgmIX+3qdYP6zwbGUBy5hN07Dw/VJLlKUJiI+L0fF
G9NfAkA3ModsZrs12+FjSe7Mr+dzmGExpcOMRIlp44RudtdBlLwKSDHi3uUG7mb0
K+7ADIgg237t0VZp2pgXl6MblLyNAkEAsXLi/ZF/Af3RDfxW8bzdWWlyyiIrBJAE
yRlEJkTJdrB7rGrvOSnWwkDDvh3qnJg23021xDOLZ8KKs8nY+wPB/wJBAJELPknX
tb+vcj+CnOJW2B13x0ljRyJOOBGmJXPd8urJ/bv3w1eSL3yRqXi4PwGASvXePGYW
SHaLNuPgGNPKEgY=
-----END PRIVATE KEY-----`;
const RSA_PUBLIC = `-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDBodJwX0LUY8g0ar012FlanvDr
ygxczuixRA8P9flAh201+dM18lxcRnLm0ZX3OdVJ7pN6lD6cSi2xdvPV4zbZc9yO
tJgB+pNjYvTz+4JNFShNqjNyFzsqS4IFGs4luawsEV3EIjnS5+A8z8RVnWTLBjdo
FryhGzqla9APB8Ov0wIDAQAB
-----END PUBLIC KEY-----`;
const EC_PRIVATE = `-----BEGIN PRIVATE KEY-----
MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgyGzH13dJ+NY3FrCG
tggV3WI5jnLqdN/Ai2Rih/GTdqyhRANCAATnxdpL/BY/P/AH60DTJLjiJGAiYBym
7D9MahLMloNfl1CAIBj2f1vz8DEw1JA1w91eR8bfydmfcZSG8Vh8K8Ds
-----END PRIVATE KEY-----`;
const EC_PUBLIC = `-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE58XaS/wWPz/wB+tA0yS44iRgImAc
puw/TGoSzJaDX5dQgCAY9n9b8/AxMNSQNcPdXkfG38nZn3GUhvFYfCvA7A==
-----END PUBLIC KEY-----`;
const FOX = 'The quick brown fox jumps over the lazy dog';
const invalid = expect.objectContaining({ code: 'invalid_argument' });

describe('digests, macs and key derivation', () => {
  it('match the published vectors', () => {
    expect(crypto.digest('md5', 'abc')).toBe('900150983cd24fb0d6963f7d28e17f72');
    expect(crypto.digest('md5', '')).toBe('d41d8cd98f00b204e9800998ecf8427e');
    expect(crypto.digest('sha1', 'abc')).toBe('a9993e364706816aba3e25717850c26c9cd0d89d');
    expect(crypto.sha256('abc')).toBe('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad');
    expect(crypto.sha256(Uint8Array.of(97, 98, 99))).toBe(crypto.sha256('abc'));
    expect(crypto.digest('sha512', 'abc')).toBe('ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f');
    expect(crypto.digest('sha256', 'abc', { encoding: 'base64' })).toBe(Buffer.from(crypto.sha256('abc'), 'hex').toString('base64'));
    expect(crypto.digest('md5', 'abc', { encoding: 'base64url' })).toBe('kAFQmDzST7DWlj99KOF_cg');
    expect(crypto.digest('md5', 'abc', { encoding: 'bytes' })).toHaveLength(16);
    expect(crypto.hmac('key', FOX)).toBe('f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8');
    expect(crypto.hmac(Uint8Array.of(107, 101, 121), FOX)).toBe(crypto.hmac('key', FOX));
    expect(crypto.hmac('key', FOX, { algorithm: 'md5' })).toBe('80070713463e7749b90c2dc24911e275');
    expect(crypto.hmac('key', FOX, 'sha1')).toBe('de7c9b85b8b78aa6bc8a7a36f70a90701c9db4d9');
    expect(crypto.pbkdf2({ password: 'password', salt: 'salt', iterations: 2, length: 20, hash: 'sha1' })).toBe('ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957');
    expect(crypto.hkdf({ key: { hex: '0b'.repeat(22) }, salt: { hex: '000102030405060708090a0b0c' }, info: { hex: 'f0f1f2f3f4f5f6f7f8f9' }, length: 42 })).toBe(
      '3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865',
    );
    expect(() => crypto.digest('md4' as never, 'x')).toThrow(invalid);
  });
});

describe('AES', () => {
  const iv = { hex: '000102030405060708090a0b0c0d0e0f' };
  const pt = { hex: '6bc1bee22e409f96e93d7e117393172a' };
  it('matches the NIST vectors', () => {
    expect(crypto.aes.encrypt({ mode: 'cbc', key: { hex: '2b7e151628aed2a6abf7158809cf4f3c' }, iv, data: pt, padding: 'none', encoding: 'hex' })).toBe('7649abac8119b246cee98e9b12e9197d');
    expect(crypto.aes.encrypt({ mode: 'cbc', key: { hex: '603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4' }, iv, data: pt, padding: 'none', encoding: 'hex' })).toBe('f58c4c04d6e5f1ba779eabfb5f7bfbd6');
    expect(crypto.aes.encrypt({ mode: 'ecb', key: { hex: '2b7e151628aed2a6abf7158809cf4f3c' }, data: pt, padding: 'none', encoding: 'hex' })).toBe('3ad77bb40d7a3660a89ecaf32466ef97');
    const z = { hex: '0'.repeat(32) };
    const nonce = { hex: '0'.repeat(24) };
    const gcm = crypto.aes.encrypt({ mode: 'gcm', key: z, iv: nonce, data: z, encoding: 'hex' });
    expect(gcm).toBe('0388dace60b6a392f328c2b971b2fe78ab6e47d42cec13bdf53a67b21257bddf');
    expect(crypto.aes.decrypt({ mode: 'gcm', key: z, iv: nonce, data: { hex: gcm }, encoding: 'hex' })).toBe('0'.repeat(32));
  });
  it('round-trips text, takes aad and refuses bad input', () => {
    const key = '0123456789abcdef';
    const ct = crypto.aes.encrypt({ key, iv: key, data: 'hello qoder' });
    const c = nodeCrypto.createCipheriv('aes-128-cbc', key, key);
    expect(ct).toBe(Buffer.concat([c.update('hello qoder'), c.final()]).toString('base64'));
    expect(crypto.aes.decrypt({ key, iv: key, data: { base64: ct }, encoding: 'utf8' })).toBe('hello qoder');
    expect(crypto.aes.decrypt({ key, iv: key, data: { base64: ct } })).toHaveLength(11);
    const z = { hex: '0'.repeat(32) };
    const nonce = { hex: '0'.repeat(24) };
    const sealed = crypto.aes.encrypt({ mode: 'gcm', key: z, iv: nonce, data: 'x', aad: 'meta' });
    expect(crypto.aes.decrypt({ mode: 'gcm', key: z, iv: nonce, data: { base64: sealed }, aad: 'meta', encoding: 'utf8' })).toBe('x');
    expect(() => crypto.aes.decrypt({ mode: 'gcm', key: z, iv: nonce, data: { base64: sealed }, aad: 'other' })).toThrow(invalid);
    expect(() => crypto.aes.decrypt({ mode: 'gcm', key: 'abc', iv: nonce, data: { base64: sealed } })).toThrow(invalid);
    expect(() => crypto.aes.decrypt({ mode: 'gcm', key: z, iv: 'x', data: { base64: sealed } })).toThrow(invalid);
  });
});

describe('RSA and ECDSA', () => {
  it('encrypt with PKCS#1 and OAEP and sign with RSA / ECDSA, checked by node', () => {
    const priv = nodeCrypto.createPrivateKey(RSA_PRIVATE);
    const pkcs1 = Buffer.from(crypto.rsa.encrypt({ key: RSA_PUBLIC, data: 'secret-key-16byt', padding: 'pkcs1', encoding: 'bytes' }));
    expect(nodeCrypto.privateDecrypt({ key: priv, padding: nodeCrypto.constants.RSA_PKCS1_PADDING }, pkcs1).toString()).toBe('secret-key-16byt');
    const oaep = Buffer.from(crypto.rsa.encrypt({ key: RSA_PUBLIC, data: Uint8Array.of(1, 2, 3), padding: 'oaep', hash: 'sha256', encoding: 'base64url' }), 'base64url');
    expect([...nodeCrypto.privateDecrypt({ key: priv, padding: nodeCrypto.constants.RSA_PKCS1_OAEP_PADDING, oaepHash: 'sha256' }, oaep)]).toEqual([1, 2, 3]);
    const sig = crypto.sign({ algorithm: 'rsa-sha256', key: RSA_PRIVATE, data: 'payload' });
    expect(nodeCrypto.verify('sha256', Buffer.from('payload'), RSA_PUBLIC, Buffer.from(sig, 'base64'))).toBe(true);
    expect(crypto.verify({ algorithm: 'rsa-sha256', key: RSA_PUBLIC, data: 'payload', signature: { base64: sig } })).toBe(true);
    expect(crypto.verify({ algorithm: 'rsa-sha256', key: RSA_PUBLIC, data: 'payloaD', signature: { base64: sig } })).toBe(false);
    const ec = crypto.sign({ algorithm: 'ecdsa-p256-sha256', key: EC_PRIVATE, data: 'payload' });
    expect(nodeCrypto.verify('sha256', Buffer.from('payload'), EC_PUBLIC, Buffer.from(ec, 'base64'))).toBe(true);
    expect(crypto.sign({ algorithm: 'ecdsa-p256-sha256', key: EC_PRIVATE, data: 'payload', format: 'raw', encoding: 'bytes' })).toHaveLength(64);
    expect(crypto.verify({ algorithm: 'ecdsa-p256-sha256', key: EC_PUBLIC, data: 'payload', signature: { base64: ec } })).toBe(true);
    const pss = crypto.sign({ algorithm: 'rsa-pss-sha256', key: RSA_PRIVATE, data: 'payload', encoding: 'hex' });
    expect(crypto.verify({ algorithm: 'rsa-pss-sha256', key: RSA_PUBLIC, data: 'payload', signature: { hex: pss } })).toBe(true);
  });
  it('refuse bad keys, algorithms and sizes with invalid_argument', () => {
    expect(() => crypto.sign({ algorithm: 'rsa-sha256', key: 'not a key', data: 'x' })).toThrow(invalid);
    expect(() => crypto.sign({ algorithm: 'ecdsa-p384-sha384', key: EC_PRIVATE, data: 'x' })).toThrow(invalid);
    expect(() => crypto.rsa.encrypt({ key: RSA_PUBLIC, data: 'x'.repeat(200) })).toThrow(invalid);
    expect(() => crypto.sign({ algorithm: 'dsa' as never, key: RSA_PRIVATE, data: 'x' })).toThrow(invalid);
  });
});

describe('encoding helpers', () => {
  it('encode and decode base64, base64url and hex without Buffer', () => {
    const b = Uint8Array.of(251, 255, 254, 0, 1);
    expect(encoding.base64.encode(b)).toBe('+//+AAE=');
    expect(encoding.base64url.encode(b)).toBe('-__-AAE');
    expect(encoding.hex.encode(b)).toBe('fbfffe0001');
    expect(encoding.base64.encode('hi')).toBe('aGk=');
    expect([...encoding.base64.decode('+//+AAE')]).toEqual([...b]);
    expect([...encoding.base64url.decode('-__-AAE=')]).toEqual([...b]);
    expect([...encoding.hex.decode('fbFFfe0001')]).toEqual([...b]);
    expect(encoding.utf8.decode(encoding.utf8.encode('é€'))).toBe('é€');
    expect(() => encoding.hex.decode('abc')).toThrow(invalid);
  });
});

describe('http.fetch in the harness', () => {
  it('hands redirect to the host and returns setCookies', async () => {
    const plugin = definePlugin({
      activate() {
        commands.register('go', async () => {
          const r = await http.fetch('https://sso.example.com/start', { redirect: 'manual', headers: { cookie: 'a=1' } });
          return { status: r.status, cookies: r.setCookies, location: r.headers.location };
        });
      },
    });
    let seen: { redirect?: string; headers: Record<string, string> } | undefined;
    const out = await runCommand(plugin, 'go', {}, {
      permissions: ['net:https://sso.example.com'],
      fetch: async (req) => { seen = req; return { status: 302, headers: { location: 'https://sso.example.com/next' }, setCookies: ['sid=1; Path=/', 'x=2'] }; },
    });
    expect(out).toEqual({ status: 302, cookies: ['sid=1; Path=/', 'x=2'], location: 'https://sso.example.com/next' });
    expect(seen?.redirect).toBe('manual');
    expect(seen?.headers.cookie).toBe('a=1');
  });
});
