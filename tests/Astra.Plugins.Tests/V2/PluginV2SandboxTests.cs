using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Astra.Plugins.Tests.V2;

/// <summary>The sandbox's web globals, crypto and encoding helpers and http.fetch's redirect / cookie rules, in the real Jaunt engine
/// (the same vectors run against the Node harness in packages/plugin-sdk/test/crypto.test.ts).</summary>
public class PluginV2SandboxTests
{
    internal const string RsaPrivate = """
-----BEGIN PRIVATE KEY-----
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
-----END PRIVATE KEY-----
""";
    internal const string RsaPublic = """
-----BEGIN PUBLIC KEY-----
MIGfMA0GCSqGSIb3DQEBAQUAA4GNADCBiQKBgQDBodJwX0LUY8g0ar012FlanvDr
ygxczuixRA8P9flAh201+dM18lxcRnLm0ZX3OdVJ7pN6lD6cSi2xdvPV4zbZc9yO
tJgB+pNjYvTz+4JNFShNqjNyFzsqS4IFGs4luawsEV3EIjnS5+A8z8RVnWTLBjdo
FryhGzqla9APB8Ov0wIDAQAB
-----END PUBLIC KEY-----
""";
    internal const string EcPrivate = """
-----BEGIN PRIVATE KEY-----
MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgyGzH13dJ+NY3FrCG
tggV3WI5jnLqdN/Ai2Rih/GTdqyhRANCAATnxdpL/BY/P/AH60DTJLjiJGAiYBym
7D9MahLMloNfl1CAIBj2f1vz8DEw1JA1w91eR8bfydmfcZSG8Vh8K8Ds
-----END PRIVATE KEY-----
""";
    internal const string EcPublic = """
-----BEGIN PUBLIC KEY-----
MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE58XaS/wWPz/wB+tA0yS44iRgImAc
puw/TGoSzJaDX5dQgCAY9n9b8/AxMNSQNcPdXkfG38nZn3GUhvFYfCvA7A==
-----END PUBLIC KEY-----
""";

    private const string Fox = "The quick brown fox jumps over the lazy dog";

    private const string Code = """
        import { commands, definePlugin, crypto, encoding, http } from 'astra';
        const hex = (u) => encoding.hex.encode(u);
        export default definePlugin({
          activate() {
            const reg = (id, fn) => commands.register(id, async (a) => { try { return await fn(a); } catch (e) { return { threw: e.name + ':' + (e.code ?? '') + ':' + e.message }; } });
            reg('probe', () => Object.fromEntries(['TextEncoder', 'TextDecoder', 'atob', 'btoa', 'URL', 'URLSearchParams', 'structuredClone', 'queueMicrotask', 'DOMException', 'BigInt', 'Intl', 'Buffer'].map((n) => [n, typeof globalThis[n]])));
            reg('text', () => {
              const enc = new TextEncoder().encode('héllo €😀');
              const dec = new TextDecoder();
              const out = { bytes: hex(enc), back: dec.decode(enc), bom: new TextDecoder().decode(Uint8Array.of(0xef, 0xbb, 0xbf, 0x61)), keepBom: new TextDecoder('utf-8', { ignoreBOM: true }).decode(Uint8Array.of(0xef, 0xbb, 0xbf, 0x61)).length, lossy: new TextDecoder().decode(Uint8Array.of(0x61, 0xff, 0x62)) };
              const s = new TextDecoder(); out.streamed = s.decode(enc.subarray(0, 8), { stream: true }) + s.decode(enc.subarray(8));
              try { new TextDecoder('utf-8', { fatal: true }).decode(Uint8Array.of(0xff)); out.fatal = 'no'; } catch (e) { out.fatal = e.name; }
              try { new TextDecoder('latin2'); out.label = 'no'; } catch (e) { out.label = e.name; }
              const dest = new Uint8Array(5); const r = new TextEncoder().encodeInto('a€€', dest); out.into = [r.read, r.written, hex(dest)];
              out.b = btoa('hello') + '|' + btoa('é') + '|' + atob('aGVsbG8=') + '|' + (atob(' 6Q ') === 'é') + '|' + atob('YQ');
              for (const bad of ['a', 'ab=c', '*']) { try { atob(bad); out['atob ' + bad] = 'no'; } catch (e) { out['atob ' + bad] = e.name; } }
              try { btoa('€'); out.btoa = 'no'; } catch (e) { out.btoa = e.name + e.code; }
              out.big = String(2n ** 70n);
              return out;
            });
            reg('url', () => {
              const u = new URL('../x?y=1#z', 'https://user:pw@Example.COM:443/a/b/c');
              const out = { href: u.href, host: u.host, port: u.port, origin: u.origin, user: u.username, pw: u.password, search: u.search, hash: u.hash };
              const v = new URL("http://example.com:8080/a b/../c d?q=a b&r='#f g");
              out.v = [v.href, v.pathname, v.origin];
              out.mailto = [new URL('mailto:a@b.c?x=1').pathname, new URL('mailto:a@b.c?x=1').search, new URL('data:text/plain,hi').protocol];
              out.ip6 = new URL('http://[2001:db8:0:0:0:0:0:1]:3000/').host;
              out.ip4 = new URL('HTTP://0x7f.1/').host + ' ' + new URL('http://Ünï.example/').hostname;
              out.rel = [new URL('//other.example/p', 'https://a.example/').href, new URL('?z=1', 'https://a.example/p/q?old#h').href, new URL('#n', 'https://a.example/p?q').href, new URL('c', 'https://a.example/a/b').href, new URL('/abs/./x/../y', 'https://a.example/a/b').href, new URL('HTTPS://A.example').href];
              out.canParse = [URL.canParse('/x'), URL.canParse('/x', 'http://a.b'), URL.canParse('nope')];
              try { new URL('/x'); out.err = 'no'; } catch (e) { out.err = e.name; }
              const w = new URL('https://a.example/p?a=1'); w.searchParams.append('b', 'x y&z'); w.searchParams.delete('a'); out.live = w.href;
              w.search = '?k=v'; out.live2 = w.searchParams.get('k'); w.pathname = '/new path'; w.hash = 'frag'; w.port = '8443'; w.protocol = 'http'; w.hostname = 'B.example'; out.set = w.href;
              const p = new URLSearchParams('a=1&b=%20x+y&a=2&%C3%A9=%E2%82%AC&=&c');
              out.p = [p.getAll('a'), p.get('b'), p.get('é'), p.has('c'), p.get('c'), p.size, p.has('a', '2'), p.has('a', '3')];
              const q = new URLSearchParams({ q: 'a b&c', e: 'é', s: "it's*-._~!" }); out.q = q.toString();
              const sorted = new URLSearchParams([['b', '2'], ['a', '1'], ['b', '1']]); sorted.sort(); out.sorted = sorted.toString();
              out.iter = [...new URLSearchParams('x=1&y=2')].join(';') + JSON.stringify(new URL('http://a.b/').toJSON());
              return out;
            });
            reg('misc', () => {
              const o = { a: [1, { b: new Date(0) }], m: new Map([[1, new Set([2])]]), u: Uint8Array.of(1, 2) }; o.self = o;
              const c = structuredClone(o);
              const r = { same: c !== o && c.self === c && c.a[1].b instanceof Date && c.a[1].b.getTime() === 0 && c.m.get(1).has(2) && c.u[1] === 2 && c.u !== o.u };
              try { structuredClone({ f() {} }); r.fn = 'no'; } catch (e) { r.fn = e.name; }
              r.order = [];
              queueMicrotask(() => r.order.push('micro'));
              r.order.push('sync');
              return new Promise((res) => setTimeout(() => res(r), 5));
            });
            reg('encoding', () => {
              const b = Uint8Array.of(251, 255, 254, 0, 1);
              return { b64: encoding.base64.encode(b), b64u: encoding.base64url.encode(b), hex: encoding.hex.encode(b), str: encoding.base64.encode('hi'), dec: [...encoding.base64.decode('+//+AAE')], decu: [...encoding.base64url.decode('-__-AAE=')], dech: [...encoding.hex.decode('fbFFfe0001')], u8: encoding.utf8.decode(encoding.utf8.encode('é€')) };
            });
            reg('hashes', () => ({
              md5: crypto.digest('md5', 'abc'), md5e: crypto.digest('md5', ''), sha1: crypto.digest('sha1', 'abc'), sha256: crypto.sha256('abc'), sha256b: crypto.sha256(Uint8Array.of(97, 98, 99)),
              sha512: crypto.digest('sha512', 'abc'), b64: crypto.digest('sha256', 'abc', { encoding: 'base64' }), b64u: crypto.digest('md5', 'abc', { encoding: 'base64url' }), bytes: crypto.digest('md5', 'abc', { encoding: 'bytes' }).length,
              hmac: crypto.hmac('key', 'The quick brown fox jumps over the lazy dog'), hmacMd5: crypto.hmac('key', 'The quick brown fox jumps over the lazy dog', { algorithm: 'md5' }),
              hmacSha1: crypto.hmac('key', 'The quick brown fox jumps over the lazy dog', 'sha1'), hmacKeyBytes: crypto.hmac(Uint8Array.of(107, 101, 121), 'The quick brown fox jumps over the lazy dog'),
              pbkdf2: crypto.pbkdf2({ password: 'password', salt: 'salt', iterations: 2, length: 20, hash: 'sha1' }),
              hkdf: crypto.hkdf({ key: { hex: '0b'.repeat(22) }, salt: { hex: '000102030405060708090a0b0c' }, info: { hex: 'f0f1f2f3f4f5f6f7f8f9' }, length: 42 }),
              bad: (() => { try { crypto.digest('md4', 'x'); } catch (e) { return e.code; } })(),
            }));
            reg('aes', () => {
              const iv = { hex: '000102030405060708090a0b0c0d0e0f' };
              const pt = { hex: '6bc1bee22e409f96e93d7e117393172a' };
              const out = {
                cbc128: crypto.aes.encrypt({ mode: 'cbc', key: { hex: '2b7e151628aed2a6abf7158809cf4f3c' }, iv, data: pt, padding: 'none', encoding: 'hex' }),
                cbc256: crypto.aes.encrypt({ mode: 'cbc', key: { hex: '603deb1015ca71be2b73aef0857d77811f352c073b6108d72d9810a30914dff4' }, iv, data: pt, padding: 'none', encoding: 'hex' }),
              };
              const key = '0123456789abcdef';
              const ct = crypto.aes.encrypt({ key, iv: key, data: 'hello qoder' });
              out.cbcText = ct; out.cbcBack = crypto.aes.decrypt({ key, iv: key, data: { base64: ct }, encoding: 'utf8' }); out.cbcBytes = crypto.aes.decrypt({ key, iv: key, data: { base64: ct } }).length;
              const z = { hex: '0'.repeat(32) }; const nonce = { hex: '0'.repeat(24) };
              out.gcm = crypto.aes.encrypt({ mode: 'gcm', key: z, iv: nonce, data: z, encoding: 'hex' });
              out.gcmBack = crypto.aes.decrypt({ mode: 'gcm', key: z, iv: nonce, data: { hex: out.gcm }, encoding: 'hex' });
              const aad = crypto.aes.encrypt({ mode: 'gcm', key: z, iv: nonce, data: 'x', aad: 'meta' });
              out.gcmAad = crypto.aes.decrypt({ mode: 'gcm', key: z, iv: nonce, data: { base64: aad }, aad: 'meta', encoding: 'utf8' });
              for (const [label, o] of [['wrongAad', { aad: 'other' }], ['shortKey', { key: 'abc' }], ['badIv', { iv: 'x' }]]) {
                try { crypto.aes.decrypt({ mode: 'gcm', key: z, iv: nonce, data: { base64: aad }, ...o }); out[label] = 'no'; } catch (e) { out[label] = e.code; }
              }
              out.ecb = crypto.aes.encrypt({ mode: 'ecb', key: { hex: '2b7e151628aed2a6abf7158809cf4f3c' }, data: pt, padding: 'none', encoding: 'hex' });
              return out;
            });
            reg('rsa', ({ pub, priv, ecPriv }) => {
              const out = {};
              out.pkcs1 = crypto.rsa.encrypt({ key: pub, data: 'secret-key-16byt', padding: 'pkcs1' });
              out.oaep = crypto.rsa.encrypt({ key: pub, data: Uint8Array.of(1, 2, 3), padding: 'oaep', hash: 'sha256', encoding: 'base64url' });
              out.sig = crypto.sign({ algorithm: 'rsa-sha256', key: priv, data: 'payload' });
              out.sigB = crypto.sign({ algorithm: 'rsa-sha256', key: priv, data: 'payload' }) === out.sig;
              out.ec = crypto.sign({ algorithm: 'ecdsa-p256-sha256', key: ecPriv, data: 'payload' });
              out.ecRaw = crypto.sign({ algorithm: 'ecdsa-p256-sha256', key: ecPriv, data: 'payload', format: 'raw', encoding: 'bytes' }).length;
              out.pss = crypto.sign({ algorithm: 'rsa-pss-sha256', key: priv, data: 'payload', encoding: 'hex' });
              for (const [label, f] of [['badKey', () => crypto.sign({ algorithm: 'rsa-sha256', key: 'not a key', data: 'x' })], ['wrongCurve', () => crypto.sign({ algorithm: 'ecdsa-p384-sha384', key: ecPriv, data: 'x' })], ['bigData', () => crypto.rsa.encrypt({ key: pub, data: 'x'.repeat(200) })], ['alg', () => crypto.sign({ algorithm: 'dsa', key: priv, data: 'x' })]]) {
                try { f(); out[label] = 'no'; } catch (e) { out[label] = e.code; }
              }
              return out;
            });
            reg('verify', ({ pub, ecPub, sig, ec }) => ({
              rsa: crypto.verify({ algorithm: 'rsa-sha256', key: pub, data: 'payload', signature: { base64: sig } }), rsaBad: crypto.verify({ algorithm: 'rsa-sha256', key: pub, data: 'payloaD', signature: { base64: sig } }),
              ec: crypto.verify({ algorithm: 'ecdsa-p256-sha256', key: ecPub, data: 'payload', signature: { base64: ec } }),
            }));
          },
        });
        """;

    private static async Task<(V2TestHost Host, Func<string, JsonNode?, Task<JsonNode>> Run)> Start(string id = "acme.box")
    {
        var t = new V2TestHost();
        var ids = new[] { "probe", "text", "url", "misc", "encoding", "hashes", "aes", "rsa", "verify" };
        await t.AddAsync(id, new JsonObject
        {
            ["main"] = "main.js",
            ["contributes"] = new JsonObject { ["commands"] = new JsonArray([.. ids.Select(c => (JsonNode?)new JsonObject { ["id"] = c })]) },
        }, [("main.js", Code)]);
        return (t, async (cmd, args) => (await t.Host.ExecuteCommandAsync(id + ":" + cmd, args ?? new JsonObject(), null, default))!);
    }

    private static string S(JsonNode? n) => n!.GetValue<string>();

    private static string[] Strs(JsonNode? n) => n!.AsArray().Select(x => x!.ToString()).ToArray();

    [Fact]
    public async Task The_engine_gets_the_web_globals_it_lacked()
    {
        var (t, run) = await Start();
        await using var _ = t;
        var p = await run("probe", null);
        foreach (var n in new[] { "TextEncoder", "TextDecoder", "URL", "URLSearchParams", "DOMException" }) Assert.Equal("function", S(p[n]));
        foreach (var n in new[] { "atob", "btoa", "structuredClone", "queueMicrotask", "BigInt" }) Assert.Equal("function", S(p[n]));
        Assert.Equal("object", S(p["Intl"]));
        Assert.Equal("undefined", S(p["Buffer"])); // not a web global: plugins use astra.encoding
    }

    [Fact]
    public async Task Text_codecs_and_base64_follow_the_web_platform()
    {
        var (t, run) = await Start();
        await using var _ = t;
        var r = await run("text", null);
        Assert.Equal(Convert.ToHexStringLower("héllo €😀"u8.ToArray()), S(r["bytes"]));
        Assert.Equal("héllo €😀", S(r["back"]));
        Assert.Equal("a", S(r["bom"]));
        Assert.Equal(2, r["keepBom"]!.GetValue<int>());
        Assert.Equal("a�b", S(r["lossy"]));
        Assert.Equal("héllo €😀", S(r["streamed"]));
        Assert.Equal("TypeError", S(r["fatal"]));
        Assert.Equal("RangeError", S(r["label"]));
        Assert.Equal(new[] { "2", "4", "61e282ac00" }, Strs(r["into"])); // 'a€' fits, the second € does not
        Assert.Equal("aGVsbG8=|6Q==|hello|true|a", S(r["b"]));
        Assert.Equal("InvalidCharacterError", S(r["atob a"]));
        Assert.Equal("InvalidCharacterError", S(r["atob ab=c"]));
        Assert.Equal("InvalidCharacterError", S(r["atob *"]));
        Assert.Equal("InvalidCharacterError5", S(r["btoa"]));
        Assert.Equal("1180591620717411303424", S(r["big"]));
    }

    [Fact]
    public async Task URL_and_URLSearchParams_parse_resolve_and_stay_live()
    {
        var (t, run) = await Start();
        await using var _ = t;
        var r = await run("url", null);
        Assert.Null(r["threw"]);
        Assert.Equal("https://user:pw@example.com/a/x?y=1#z", S(r["href"]));
        Assert.Equal("example.com", S(r["host"]));
        Assert.Equal("", S(r["port"]));
        Assert.Equal("https://example.com", S(r["origin"]));
        Assert.Equal(("user", "pw", "?y=1", "#z"), (S(r["user"]), S(r["pw"]), S(r["search"]), S(r["hash"])));
        Assert.Equal("http://example.com:8080/c%20d?q=a%20b&r=%27#f%20g", Strs(r["v"])[0]);
        Assert.Equal("/c%20d", Strs(r["v"])[1]);
        Assert.Equal(new[] { "a@b.c", "?x=1", "data:" }, Strs(r["mailto"]));
        Assert.Equal("[2001:db8::1]:3000", S(r["ip6"]));
        Assert.Equal("127.0.0.1 xn--n-nga1b.example".Split(' '), S(r["ip4"]).Split(' '));
        Assert.Equal(new[] { "https://other.example/p", "https://a.example/p/q?z=1", "https://a.example/p?q#n", "https://a.example/a/c", "https://a.example/abs/y", "https://a.example/" }, Strs(r["rel"]));
        Assert.Equal(new[] { "false", "true", "false" }, Strs(r["canParse"]));
        Assert.Equal("TypeError", S(r["err"]));
        Assert.Equal("https://a.example/p?b=x+y%26z", S(r["live"]));
        Assert.Equal("v", S(r["live2"]));
        Assert.Equal("http://b.example:8443/new%20path?k=v#frag", S(r["set"]));
        var p = r["p"]!.AsArray();
        Assert.Equal(new[] { "1", "2" }, Strs(p[0]));
        Assert.Equal(" x y", S(p[1]));
        Assert.Equal("€", S(p[2]));
        Assert.Equal(new[] { "true", "", "6", "true", "false" }, new[] { p[3]!.ToString(), S(p[4]), p[5]!.ToString(), p[6]!.ToString(), p[7]!.ToString() });
        Assert.Equal("q=a+b%26c&e=%C3%A9&s=it%27s*-._%7E%21", S(r["q"]));
        Assert.Equal("a=1&b=2&b=1", S(r["sorted"]));
        Assert.Equal("x,1;y,2\"http://a.b/\"", S(r["iter"]));
    }

    [Fact]
    public async Task Structured_clone_microtasks_and_encoding_helpers_work()
    {
        var (t, run) = await Start();
        await using var _ = t;
        var m = await run("misc", null);
        Assert.True(m["same"]!.GetValue<bool>());
        Assert.Equal("DataCloneError", S(m["fn"]));
        Assert.Equal(new[] { "sync", "micro" }, Strs(m["order"]));
        var e = await run("encoding", null);
        Assert.Equal("+//+AAE=", S(e["b64"]));
        Assert.Equal("-__-AAE", S(e["b64u"]));
        Assert.Equal("fbfffe0001", S(e["hex"]));
        Assert.Equal("aGk=", S(e["str"]));
        Assert.Equal(new[] { "251", "255", "254", "0", "1" }, Strs(e["dec"]));
        Assert.Equal(Strs(e["dec"]), Strs(e["decu"]));
        Assert.Equal(Strs(e["dec"]), Strs(e["dech"]));
        Assert.Equal("é€", S(e["u8"]));
    }

    [Fact]
    public async Task Digests_macs_and_key_derivation_match_the_published_vectors()
    {
        var (t, run) = await Start();
        await using var _ = t;
        var r = await run("hashes", null);
        Assert.Null(r["threw"]);
        Assert.Equal("900150983cd24fb0d6963f7d28e17f72", S(r["md5"]));
        Assert.Equal("d41d8cd98f00b204e9800998ecf8427e", S(r["md5e"]));
        Assert.Equal("a9993e364706816aba3e25717850c26c9cd0d89d", S(r["sha1"]));
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", S(r["sha256"]));
        Assert.Equal(S(r["sha256"]), S(r["sha256b"]));
        Assert.Equal("ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f", S(r["sha512"]));
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad")), S(r["b64"]));
        Assert.Equal("kAFQmDzST7DWlj99KOF_cg", S(r["b64u"]));
        Assert.Equal(16, r["bytes"]!.GetValue<int>());
        Assert.Equal("f7bc83f430538424b13298e6aa6fb143ef4d59a14946175997479dbc2d1a3cd8", S(r["hmac"]));
        Assert.Equal(S(r["hmac"]), S(r["hmacKeyBytes"]));
        Assert.Equal("80070713463e7749b90c2dc24911e275", S(r["hmacMd5"]));
        Assert.Equal("de7c9b85b8b78aa6bc8a7a36f70a90701c9db4d9", S(r["hmacSha1"]));
        Assert.Equal("ea6c014dc72d6f8ccd1ed92ace1d41f0d8de8957", S(r["pbkdf2"]));
        Assert.Equal("3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865", S(r["hkdf"]));
        Assert.Equal("invalid_argument", S(r["bad"]));
    }

    [Fact]
    public async Task AES_matches_the_NIST_vectors_and_refuses_bad_input()
    {
        var (t, run) = await Start();
        await using var _ = t;
        var r = await run("aes", null);
        Assert.Null(r["threw"]);
        Assert.Equal("7649abac8119b246cee98e9b12e9197d", S(r["cbc128"]));
        Assert.Equal("f58c4c04d6e5f1ba779eabfb5f7bfbd6", S(r["cbc256"]));
        Assert.Equal("3ad77bb40d7a3660a89ecaf32466ef97", S(r["ecb"]));
        using (var aes = Aes.Create())
        {
            aes.Key = "0123456789abcdef"u8.ToArray();
            Assert.Equal(Convert.ToBase64String(aes.EncryptCbc("hello qoder"u8, "0123456789abcdef"u8)), S(r["cbcText"]));
        }
        Assert.Equal("hello qoder", S(r["cbcBack"]));
        Assert.Equal(11, r["cbcBytes"]!.GetValue<int>());
        Assert.Equal("0388dace60b6a392f328c2b971b2fe78ab6e47d42cec13bdf53a67b21257bddf", S(r["gcm"]));
        Assert.Equal("00000000000000000000000000000000", S(r["gcmBack"]));
        Assert.Equal("x", S(r["gcmAad"]));
        Assert.Equal("invalid_argument", S(r["wrongAad"]));
        Assert.Equal("invalid_argument", S(r["shortKey"]));
        Assert.Equal("invalid_argument", S(r["badIv"]));
    }

    [Fact]
    public async Task RSA_and_ECDSA_round_trip_against_the_platform_and_refuse_bad_keys()
    {
        var (t, run) = await Start();
        await using var _ = t;
        var args = new JsonObject { ["pub"] = RsaPublic, ["priv"] = RsaPrivate, ["ecPriv"] = EcPrivate };
        var r = await run("rsa", args);
        Assert.Null(r["threw"]);
        using var rsa = RSA.Create();
        rsa.ImportFromPem(RsaPrivate);
        Assert.Equal("secret-key-16byt", System.Text.Encoding.UTF8.GetString(rsa.Decrypt(Convert.FromBase64String(S(r["pkcs1"])), RSAEncryptionPadding.Pkcs1)));
        var oaep = Convert.FromBase64String(S(r["oaep"]).Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(new byte[] { 1, 2, 3 }, rsa.Decrypt(oaep, RSAEncryptionPadding.OaepSHA256));
        var sig = Convert.FromBase64String(S(r["sig"]));
        Assert.True(rsa.VerifyData("payload"u8, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        Assert.True(r["sigB"]!.GetValue<bool>()); // PKCS#1 v1.5 signatures are deterministic
        Assert.True(rsa.VerifyData("payload"u8, Convert.FromHexString(S(r["pss"])), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        using var ec = ECDsa.Create();
        ec.ImportFromPem(EcPublic);
        Assert.True(ec.VerifyData("payload"u8, Convert.FromBase64String(S(r["ec"])), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.Equal(64, r["ecRaw"]!.GetValue<int>());
        foreach (var bad in new[] { "badKey", "wrongCurve", "bigData", "alg" }) Assert.Equal("invalid_argument", S(r[bad]));

        var v = await run("verify", new JsonObject { ["pub"] = RsaPublic, ["ecPub"] = EcPublic, ["sig"] = S(r["sig"]), ["ec"] = S(r["ec"]) });
        Assert.True(v["rsa"]!.GetValue<bool>());
        Assert.False(v["rsaBad"]!.GetValue<bool>());
        Assert.True(v["ec"]!.GetValue<bool>());
    }
}
