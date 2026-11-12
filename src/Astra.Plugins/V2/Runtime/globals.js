// The v2 sandbox's missing web globals (design/plugin-v2 02 §sandbox globals), run once per engine before the boot: a
// function the host calls with its synchronous door and that installs, on globalThis, what the engine lacks —
// TextEncoder / TextDecoder, atob / btoa, DOMException, URL / URLSearchParams, structuredClone, queueMicrotask — and hands
// the boot the byte helpers the runtime itself uses. UTF-8 and base64 are host calls (encoding.*): a "binary string" (one
// char per byte) crosses the boundary, which String.fromCharCode / charCodeAt convert to and from a Uint8Array natively.
// A global the engine already has is left alone, so the same file is harmless under Node (the SDK's harness).
(sync) => {
  'use strict';
  const hs = (name, args) => {
    const r = JSON.parse(sync(name, JSON.stringify(args)));
    if (r && r.error) throw Object.assign(new Error(r.error.message), { code: r.error.code });
    return r ? r.result : undefined;
  };

  // ---------- bytes <-> binary strings
  const bin = (u8) => {
    let s = '';
    for (let i = 0; i < u8.length; i += 8192) s += String.fromCharCode.apply(null, u8.subarray(i, i + 8192));
    return s;
  };
  const unbin = (s) => {
    const out = new Uint8Array(s.length);
    for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
    return out;
  };
  const toBytes = (v) => {
    if (v instanceof Uint8Array) return v;
    if (ArrayBuffer.isView(v)) return new Uint8Array(v.buffer, v.byteOffset, v.byteLength);
    if (v instanceof ArrayBuffer || (typeof SharedArrayBuffer === 'function' && v instanceof SharedArrayBuffer)) return new Uint8Array(v);
    throw new TypeError('expected an ArrayBuffer or a typed array');
  };
  const utf8enc = (text) => unbin(hs('encoding.utf8Encode', { text: String(text) }));
  const utf8dec = (u8, fatal, ignoreBOM) => hs('encoding.utf8Decode', { bin: bin(u8), fatal: !!fatal, ignoreBOM: !!ignoreBOM });
  const toText = (u8, to) => hs('encoding.encode', { bin: bin(u8), to });
  const fromText = (text, from) => unbin(hs('encoding.decode', { text: String(text), from }));

  const g = globalThis;
  const def = (name, value) => {
    if (typeof g[name] === 'undefined') Object.defineProperty(g, name, { value, writable: true, configurable: true, enumerable: false });
  };

  // ---------- DOMException (the errors atob / btoa / structuredClone throw)
  if (typeof g.DOMException === 'undefined') {
    const codes = { IndexSizeError: 1, NotFoundError: 8, NotSupportedError: 9, InvalidStateError: 11, SyntaxError: 12, InvalidCharacterError: 5, DataCloneError: 25, AbortError: 20, TimeoutError: 23 };
    class DOMException extends Error {
      constructor(message = '', name = 'Error') {
        super(String(message));
        Object.defineProperty(this, 'name', { value: String(name), configurable: true });
        Object.defineProperty(this, 'code', { value: codes[name] || 0, configurable: true });
      }
    }
    def('DOMException', DOMException);
  }
  const DOMEx = g.DOMException;

  // ---------- TextEncoder / TextDecoder
  if (typeof g.TextEncoder === 'undefined') {
    class TextEncoder {
      get encoding() { return 'utf-8'; }
      encode(input = '') { return utf8enc(input); }
      encodeInto(source, dest) {
        source = String(source);
        const whole = utf8enc(source);
        if (whole.length <= dest.length) { dest.set(whole); return { read: source.length, written: whole.length }; }
        // as many whole characters as fit
        let read = 0, written = 0;
        for (const ch of source) {
          const n = ch.codePointAt(0) < 0x80 ? 1 : ch.codePointAt(0) < 0x800 ? 2 : ch.codePointAt(0) < 0x10000 ? 3 : 4;
          if (written + n > dest.length) break;
          written += n; read += ch.length;
        }
        dest.set(utf8enc(source.slice(0, read)).subarray(0, written));
        return { read, written };
      }
    }
    def('TextEncoder', TextEncoder);
  }
  if (typeof g.TextDecoder === 'undefined') {
    const labels = new Set(['utf-8', 'utf8', 'unicode-1-1-utf-8', 'unicode11utf8', 'unicode20utf8', 'x-unicode20utf8']);
    // how many trailing bytes begin a character the next chunk completes (stream: true)
    const incompleteTail = (b) => {
      for (let back = 1; back <= 3 && back <= b.length; back++) {
        const c = b[b.length - back];
        if ((c & 0xc0) === 0x80) continue;
        const need = c >= 0xf0 && c < 0xf5 ? 4 : c >= 0xe0 ? 3 : c >= 0xc2 && c < 0xe0 ? 2 : 1;
        return need > back ? back : 0;
      }
      return 0;
    };
    class TextDecoder {
      #fatal; #ignoreBOM; #pending = new Uint8Array(0); #seenBOM = false;
      constructor(label = 'utf-8', options = {}) {
        if (!labels.has(String(label).trim().toLowerCase())) throw new RangeError('The "' + label + '" encoding is not supported');
        this.#fatal = !!(options && options.fatal);
        this.#ignoreBOM = !!(options && options.ignoreBOM);
      }
      get encoding() { return 'utf-8'; }
      get fatal() { return this.#fatal; }
      get ignoreBOM() { return this.#ignoreBOM; }
      decode(input, options) {
        let bytes = input === undefined ? new Uint8Array(0) : toBytes(input);
        if (this.#pending.length) { const all = new Uint8Array(this.#pending.length + bytes.length); all.set(this.#pending); all.set(bytes, this.#pending.length); bytes = all; this.#pending = new Uint8Array(0); }
        const stream = !!(options && options.stream);
        if (stream) { const k = incompleteTail(bytes); if (k) { this.#pending = bytes.slice(bytes.length - k); bytes = bytes.subarray(0, bytes.length - k); } }
        let out;
        try { out = utf8dec(bytes, this.#fatal, true); } catch (e) { this.#seenBOM = false; throw new TypeError('The encoded data was not valid for encoding utf-8'); }
        if (!this.#ignoreBOM && !this.#seenBOM && out.length) { if (out.charCodeAt(0) === 0xfeff) out = out.slice(1); }
        if (out.length || bytes.length) this.#seenBOM = stream;
        return out;
      }
    }
    def('TextDecoder', TextDecoder);
  }

  // ---------- atob / btoa
  if (typeof g.atob === 'undefined') {
    def('atob', (data) => {
      let stripped = String(data).replace(/[\t\n\f\r ]+/g, '');
      if (stripped.length % 4 === 0) stripped = stripped.replace(/==?$/, '');
      if (/[^A-Za-z0-9+/]/.test(stripped) || stripped.length % 4 === 1) throw new DOMEx('The string to be decoded is not correctly encoded.', 'InvalidCharacterError');
      return bin(fromText(stripped, 'base64'));
    });
  }
  if (typeof g.btoa === 'undefined') {
    def('btoa', (data) => {
      const s = String(data);
      for (let i = 0; i < s.length; i++) if (s.charCodeAt(i) > 255) throw new DOMEx('The string to be encoded contains characters outside of the Latin1 range.', 'InvalidCharacterError');
      return toText(unbin(s), 'base64');
    });
  }

  // ---------- queueMicrotask / structuredClone
  def('queueMicrotask', (fn) => {
    if (typeof fn !== 'function') throw new TypeError('queueMicrotask needs a function');
    Promise.resolve().then(fn);
  });
  def('structuredClone', (value) => {
    const seen = new Map();
    const fail = (what) => { throw new DOMEx(what + ' could not be cloned.', 'DataCloneError'); };
    const walk = (v) => {
      if (typeof v === 'function' || typeof v === 'symbol') fail(typeof v === 'function' ? 'A function' : 'A symbol');
      if (v === null || typeof v !== 'object') return v;
      if (seen.has(v)) return seen.get(v);
      let out;
      if (v instanceof Date) out = new Date(v.getTime());
      else if (v instanceof RegExp) out = new RegExp(v.source, v.flags);
      else if (v instanceof Map) { out = new Map(); seen.set(v, out); for (const [k, x] of v) out.set(walk(k), walk(x)); return out; }
      else if (v instanceof Set) { out = new Set(); seen.set(v, out); for (const x of v) out.add(walk(x)); return out; }
      else if (v instanceof ArrayBuffer) out = v.slice(0);
      else if (ArrayBuffer.isView(v)) { const buf = v.buffer.slice(v.byteOffset, v.byteOffset + v.byteLength); out = v instanceof DataView ? new DataView(buf) : new v.constructor(buf); }
      else if (v instanceof Error) { out = new (['EvalError', 'RangeError', 'ReferenceError', 'SyntaxError', 'TypeError', 'URIError'].includes(v.name) ? g[v.name] : Error)(v.message); seen.set(v, out); if (v.stack !== undefined) out.stack = v.stack; if ('cause' in v) out.cause = walk(v.cause); return out; }
      else if (v instanceof Boolean || v instanceof Number || v instanceof String) out = Object(v.valueOf());
      else if (Array.isArray(v)) { out = new Array(v.length); seen.set(v, out); for (const k of Object.keys(v)) out[k] = walk(v[k]); return out; }
      else if (v instanceof Promise || v instanceof WeakMap || v instanceof WeakSet) fail('An object');
      else { out = {}; seen.set(v, out); for (const k of Object.keys(v)) out[k] = walk(v[k]); return out; }
      seen.set(v, out);
      return out;
    };
    return walk(value);
  });

  // ---------- URL / URLSearchParams (WHATWG URL Standard, the parts plugins use)
  if (typeof g.URL === 'undefined' || typeof g.URLSearchParams === 'undefined') {
    const hex = (n) => '%' + (n < 16 ? '0' : '') + n.toString(16).toUpperCase();
    // percent-encode sets over a byte
    const C0 = (b) => b <= 0x1f || b > 0x7e;
    const inList = (list) => { const s = new Set([...list].map((c) => c.charCodeAt(0))); return (b) => s.has(b); };
    const FRAGMENT = ((x) => (b) => C0(b) || x(b))(inList(' "<>`'));
    const QUERY = ((x) => (b) => C0(b) || x(b))(inList(' "#<>'));
    const SPECIAL_QUERY = ((x) => (b) => QUERY(b) || x(b))(inList("'"));
    const PATH = ((x) => (b) => QUERY(b) || x(b))(inList('?^`{}'));
    const USERINFO = ((x) => (b) => PATH(b) || x(b))(inList('/:;=@[\\]|'));
    const FORM = (b) => !((b >= 0x30 && b <= 0x39) || (b >= 0x41 && b <= 0x5a) || (b >= 0x61 && b <= 0x7a) || b === 0x2a || b === 0x2d || b === 0x2e || b === 0x5f);
    const pct = (str, set) => {
      let plain = true;
      for (let i = 0; i < str.length; i++) { const c = str.charCodeAt(i); if (c > 0x7e || set(c)) { plain = false; break; } }
      if (plain) return str;
      const bytes = utf8enc(str);
      let out = '';
      for (let i = 0; i < bytes.length; i++) out += set(bytes[i]) ? hex(bytes[i]) : String.fromCharCode(bytes[i]);
      return out;
    };
    const unpct = (str) => {
      if (str.indexOf('%') < 0) return str;
      const bytes = utf8enc(str);
      const out = [];
      for (let i = 0; i < bytes.length; i++) {
        if (bytes[i] === 0x25 && i + 2 < bytes.length && /^[0-9a-fA-F]{2}$/.test(String.fromCharCode(bytes[i + 1], bytes[i + 2]))) { out.push(parseInt(String.fromCharCode(bytes[i + 1], bytes[i + 2]), 16)); i += 2; } else out.push(bytes[i]);
      }
      return utf8dec(Uint8Array.from(out), false, true);
    };
    const SPECIAL = { 'ftp:': 21, 'file:': null, 'http:': 80, 'https:': 443, 'ws:': 80, 'wss:': 443 };
    const isSpecial = (scheme) => Object.prototype.hasOwnProperty.call(SPECIAL, scheme);

    // punycode (RFC 3492) ToASCII for a label
    const punyEncode = (input) => {
      const cps = Array.from(input, (c) => c.codePointAt(0));
      let n = 128, delta = 0, bias = 72;
      let out = cps.filter((c) => c < 128).map((c) => String.fromCharCode(c)).join('');
      let h = out.length;
      const b = h;
      if (b) out += '-';
      const digit = (d) => String.fromCharCode(d < 26 ? d + 97 : d + 22);
      const adapt = (d, num, first) => { d = first ? Math.floor(d / 700) : d >> 1; d += Math.floor(d / num); let k = 0; while (d > 455) { d = Math.floor(d / 35); k += 36; } return k + Math.floor(36 * d / (d + 38)); };
      while (h < cps.length) {
        const m = Math.min(...cps.filter((c) => c >= n));
        delta += (m - n) * (h + 1); n = m;
        for (const c of cps) {
          if (c < n) delta++;
          if (c === n) {
            let q = delta;
            for (let k = 36; ; k += 36) {
              const t = k <= bias ? 1 : k >= bias + 26 ? 26 : k - bias;
              if (q < t) break;
              out += digit(t + (q - t) % (36 - t)); q = Math.floor((q - t) / (36 - t));
            }
            out += digit(q); bias = adapt(delta, h + 1, h === b); delta = 0; h++;
          }
        }
        delta++; n++;
      }
      return out;
    };
    const toASCII = (domain) => domain.normalize('NFC').toLowerCase().split(/[.。．｡]/).map((l) => (/[^\x00-\x7f]/.test(l) ? 'xn--' + punyEncode(l) : l)).join('.');

    const parseIPv4Number = (s) => {
      if (s === '') return NaN;
      let radix = 10;
      if (/^0[xX]/.test(s)) { s = s.slice(2); radix = 16; } else if (s.length > 1 && s[0] === '0') { s = s.slice(1); radix = 8; }
      if (s === '') return 0;
      if (!(radix === 10 ? /^[0-9]+$/ : radix === 16 ? /^[0-9a-fA-F]+$/ : /^[0-7]+$/).test(s)) return NaN;
      return parseInt(s, radix);
    };
    const endsInNumber = (host) => {
      const parts = host.split('.');
      if (parts[parts.length - 1] === '') { if (parts.length === 1) return false; parts.pop(); }
      const last = parts[parts.length - 1];
      return /^[0-9]+$/.test(last) || !Number.isNaN(parseIPv4Number(last)) && /^0[xX]/.test(last);
    };
    const parseIPv4 = (host) => {
      const parts = host.split('.');
      if (parts[parts.length - 1] === '' && parts.length > 1) parts.pop();
      if (parts.length > 4) return null;
      const nums = parts.map(parseIPv4Number);
      if (nums.some(Number.isNaN)) return null;
      if (nums.slice(0, -1).some((n) => n > 255) || nums[nums.length - 1] >= 256 ** (5 - nums.length)) return null;
      let ip = nums[nums.length - 1];
      nums.slice(0, -1).forEach((n, i) => { ip += n * 256 ** (3 - i); });
      return [ip >>> 24, (ip >>> 16) & 255, (ip >>> 8) & 255, ip & 255].join('.');
    };
    const parseIPv6 = (s) => {
      const pieces = [0, 0, 0, 0, 0, 0, 0, 0];
      let idx = 0, compress = null, i = 0;
      if (s[i] === ':') { if (s[i + 1] !== ':') return null; i += 2; idx++; compress = idx; }
      while (i < s.length) {
        if (idx === 8) return null;
        if (s[i] === ':') { if (compress !== null) return null; i++; idx++; compress = idx; continue; }
        let value = 0, len = 0;
        while (len < 4 && i < s.length && /[0-9a-fA-F]/.test(s[i])) { value = value * 16 + parseInt(s[i], 16); i++; len++; }
        if (s[i] === '.') {
          if (len === 0) return null;
          i -= len;
          if (idx > 6) return null;
          const v4 = s.slice(i).split('.');
          if (v4.length !== 4 || v4.some((p) => !/^(0|[1-9][0-9]*)$/.test(p) || Number(p) > 255)) return null;
          pieces[idx++] = v4[0] * 256 + +v4[1]; pieces[idx++] = v4[2] * 256 + +v4[3]; i = s.length; break;
        } else if (s[i] === ':') { i++; if (i >= s.length) return null; } else if (i < s.length) return null;
        pieces[idx++] = value;
      }
      if (compress !== null) {
        let swaps = idx - compress; idx = 7;
        while (idx !== 0 && swaps > 0) { const t = pieces[compress + swaps - 1]; pieces[compress + swaps - 1] = pieces[idx]; pieces[idx] = t; idx--; swaps--; }
      } else if (idx !== 8) return null;
      // serialize with the longest run of zeros compressed
      let best = -1, bestLen = 1;
      for (let a = 0; a < 8;) { if (pieces[a] !== 0) { a++; continue; } let b = a; while (b < 8 && pieces[b] === 0) b++; if (b - a > bestLen) { best = a; bestLen = b - a; } a = b; }
      let out = '';
      for (let a = 0; a < 8; a++) {
        if (a === best) { out += a === 0 ? '::' : ':'; a += bestLen - 1; continue; }
        out += pieces[a].toString(16) + (a < 7 ? ':' : '');
      }
      return '[' + out + ']';
    };
    const FORBIDDEN_HOST = /[\u0000\t\n\r #/:<>?@[\\\]^|]/;
    const FORBIDDEN_DOMAIN = /[\u0000-\u001f #%/:<>?@[\\\]^|\u007f]/;
    // a host string -> its serialization, or null on failure
    const parseHost = (input, special) => {
      if (input[0] === '[') { if (input[input.length - 1] !== ']') return null; return parseIPv6(input.slice(1, -1)); }
      if (!special) return FORBIDDEN_HOST.test(input) ? null : pct(input, C0);
      const domain = toASCII(unpct(input));
      if (domain === '' || FORBIDDEN_DOMAIN.test(domain)) return null;
      if (endsInNumber(domain)) return parseIPv4(domain);
      return domain;
    };

    const emptyRecord = () => ({ scheme: '', username: '', password: '', host: null, port: null, path: [], opaque: null, query: null, fragment: null });
    const shorten = (rec) => { if (rec.scheme === 'file:' && rec.path.length === 1 && /^[A-Za-z]:$/.test(rec.path[0])) return; rec.path.pop(); };
    const isDouble = (s) => /^(\.|%2[eE])(\.|%2[eE])$/.test(s);
    const isSingle = (s) => /^(\.|%2[eE])$/.test(s);
    const pushPath = (rec, str, special) => {
      const segs = str.split(special ? /[/\\]/ : /\//);
      segs.forEach((seg, i) => {
        const last = i === segs.length - 1;
        if (isDouble(seg)) { shorten(rec); if (last) rec.path.push(''); } else if (isSingle(seg)) { if (last) rec.path.push(''); } else rec.path.push(pct(seg, PATH));
      });
    };

    // the basic URL parser: a record, or null when the input is not a URL
    const parseURL = (raw, base) => {
      let input = String(raw).replace(/^[\u0000- ]+|[\u0000- ]+$/g, '').replace(/[\t\n\r]/g, '');
      const rec = emptyRecord();
      let m = /^([A-Za-z][A-Za-z0-9+.-]*):/.exec(input);
      let rest;
      let special;
      const split = (text) => {
        let frag = null, q = null;
        const h = text.indexOf('#');
        if (h >= 0) { frag = text.slice(h + 1); text = text.slice(0, h); }
        const qi = text.indexOf('?');
        if (qi >= 0) { q = text.slice(qi + 1); text = text.slice(0, qi); }
        return [text, q, frag];
      };
      const setTail = (q, frag) => {
        if (q !== null) rec.query = pct(q, isSpecial(rec.scheme) ? SPECIAL_QUERY : QUERY);
        if (frag !== null) rec.fragment = pct(frag, FRAGMENT);
      };
      const authority = (text) => {
        // text begins after the slashes: authority then path
        const end = text.search(special ? /[/\\]/ : /\//);
        const auth = end < 0 ? text : text.slice(0, end);
        const path = end < 0 ? '' : text.slice(end);
        const at = auth.lastIndexOf('@');
        let hostport = auth;
        if (at >= 0) {
          const info = auth.slice(0, at);
          hostport = auth.slice(at + 1);
          const colon = info.indexOf(':');
          rec.username = pct(colon < 0 ? info : info.slice(0, colon), USERINFO);
          rec.password = colon < 0 ? '' : pct(info.slice(colon + 1), USERINFO);
          if (hostport === '') return null;
        }
        const pm = /:([^\]:]*)$/.exec(hostport);
        let hostStr = hostport;
        if (pm) {
          hostStr = hostport.slice(0, pm.index);
          if (pm[1] !== '') {
            if (!/^[0-9]+$/.test(pm[1]) || Number(pm[1]) > 65535) return null;
            rec.port = Number(pm[1]);
            if (SPECIAL[rec.scheme] === rec.port) rec.port = null;
          }
        }
        if (hostStr === '') { if (special && rec.scheme !== 'file:') return null; if (at >= 0 || rec.port !== null) return null; rec.host = ''; } else {
          const host = parseHost(hostStr, special);
          if (host === null) return null;
          rec.host = rec.scheme === 'file:' && host === 'localhost' ? '' : host;
        }
        return path;
      };
      if (m) {
        rec.scheme = m[0].toLowerCase();
        special = isSpecial(rec.scheme);
        rest = input.slice(m[0].length);
        const [body, q, frag] = split(rest);
        if (special) {
          if (base && base.scheme === rec.scheme && !/^[/\\]/.test(body) && rec.scheme !== 'file:') return relative(rec, base, body, q, frag, special);
          if (rec.scheme === 'file:') {
            if (/^[/\\]{2}/.test(body)) { const p = authority(body.slice(2)); if (p === null) return null; pushPath(rec, p === '' ? '/' : p, true); rec.path.shift(); } else if (/^[/\\]/.test(body)) { rec.host = ''; pushPath(rec, body, true); rec.path.shift(); } else if (base && base.scheme === 'file:') return relative(rec, base, body, q, frag, true); else { rec.host = ''; pushPath(rec, '/' + body, true); rec.path.shift(); }
          } else {
            const p = authority(body.replace(/^[/\\]*/, ''));
            if (p === null) return null;
            pushPath(rec, p === '' ? '/' : p, true); rec.path.shift();
          }
          setTail(q, frag);
          return rec;
        }
        if (body.startsWith('//')) { const p = authority(body.slice(2)); if (p === null) return null; if (p !== '') { pushPath(rec, p, false); rec.path.shift(); } } else if (body.startsWith('/')) { pushPath(rec, body, false); rec.path.shift(); } else rec.opaque = pct(body, C0);
        setTail(q, frag);
        return rec;
      }
      if (!base) return null;
      const [body, q, frag] = split(input);
      if (base.opaque !== null) {
        if (!input.startsWith('#')) return null;
        return { ...base, path: [], fragment: pct(input.slice(1), FRAGMENT) };
      }
      rec.scheme = base.scheme;
      special = isSpecial(rec.scheme);
      return relative(rec, base, body, q, frag, special);
    };
    // body is relative to base: authority, absolute path, query-only, fragment-only or a merged path
    const relative = (rec, base, body, q, frag, special) => {
      const setQ = (qv, f) => {
        if (qv !== null) rec.query = pct(qv, special ? SPECIAL_QUERY : QUERY);
        if (f !== null) rec.fragment = pct(f, FRAGMENT);
      };
      const slashes = special ? /^[/\\]{2}/ : /^\/\//;
      if (slashes.test(body)) {
        const again = parseURL(rec.scheme + body.replace(/^[/\\]{2}/, '//') + (q !== null ? '?' + q : '') + (frag !== null ? '#' + frag : ''), null);
        return again;
      }
      rec.username = base.username; rec.password = base.password; rec.host = base.host; rec.port = base.port;
      if (special ? /^[/\\]/.test(body) : body.startsWith('/')) { pushPath(rec, body, special); rec.path.shift(); setQ(q, frag); return rec; }
      rec.path = base.path.slice();
      if (body === '') { rec.query = q !== null ? null : base.query; setQ(q, frag); return rec; }
      rec.path.pop();
      pushPath(rec, body, special);
      setQ(q, frag);
      return rec;
    };

    const serializePath = (rec) => (rec.opaque !== null ? rec.opaque : rec.path.map((s) => '/' + s).join(''));
    const serialize = (rec, noFragment) => {
      let out = rec.scheme;
      if (rec.host !== null) {
        out += '//';
        if (rec.username !== '' || rec.password !== '') out += rec.username + (rec.password !== '' ? ':' + rec.password : '') + '@';
        out += rec.host + (rec.port !== null ? ':' + rec.port : '');
      } else if (rec.opaque === null && rec.path.length > 1 && rec.path[0] === '') out += '/.';
      out += serializePath(rec);
      if (rec.query !== null) out += '?' + rec.query;
      if (!noFragment && rec.fragment !== null) out += '#' + rec.fragment;
      return out;
    };

    // ----- URLSearchParams
    const parseForm = (s) => {
      const list = [];
      for (const part of s.split('&')) {
        if (part === '') continue;
        const eq = part.indexOf('=');
        const name = eq < 0 ? part : part.slice(0, eq), value = eq < 0 ? '' : part.slice(eq + 1);
        list.push([unpct(name.replace(/\+/g, ' ')), unpct(value.replace(/\+/g, ' '))]);
      }
      return list;
    };
    const formEncode = (s) => pct(s, FORM).replace(/%20/g, '+');
    const serializeForm = (list) => list.map(([k, v]) => formEncode(k) + '=' + formEncode(v)).join('&');
    const wf = (s) => (typeof s.toWellFormed === 'function' ? s.toWellFormed() : s);
    const lists = new WeakMap(); // URLSearchParams -> its list
    const owners = new WeakMap(); // URLSearchParams -> its URL's record holder
    class URLSearchParams {
      constructor(init = '') {
        const list = [];
        lists.set(this, list);
        if (init !== null && typeof init === 'object') {
          if (init instanceof URLSearchParams) for (const [k, v] of lists.get(init)) list.push([k, v]);
          else if (typeof init[Symbol.iterator] === 'function') {
            for (const pair of init) {
              const p = Array.from(pair);
              if (p.length !== 2) throw new TypeError('Each query pair must be an iterable [name, value] tuple');
              list.push([wf(String(p[0])), wf(String(p[1]))]);
            }
          } else for (const k of Reflect.ownKeys(init)) { if (typeof k === 'string' && Object.prototype.propertyIsEnumerable.call(init, k)) list.push([wf(k), wf(String(init[k]))]); }
        } else {
          const s = String(init);
          for (const p of parseForm(s[0] === '?' ? s.slice(1) : s)) list.push(p);
        }
      }
      #touch() { const o = owners.get(this); if (o) o(); }
      get size() { return lists.get(this).length; }
      append(name, value) { lists.get(this).push([wf(String(name)), wf(String(value))]); this.#touch(); }
      delete(name, value) {
        name = String(name);
        const l = lists.get(this);
        const v = value === undefined ? undefined : String(value);
        for (let i = l.length - 1; i >= 0; i--) if (l[i][0] === name && (v === undefined || l[i][1] === v)) l.splice(i, 1);
        this.#touch();
      }
      get(name) { name = String(name); const e = lists.get(this).find((p) => p[0] === name); return e ? e[1] : null; }
      getAll(name) { name = String(name); return lists.get(this).filter((p) => p[0] === name).map((p) => p[1]); }
      has(name, value) { name = String(name); const v = value === undefined ? undefined : String(value); return lists.get(this).some((p) => p[0] === name && (v === undefined || p[1] === v)); }
      set(name, value) {
        name = wf(String(name)); value = wf(String(value));
        const l = lists.get(this);
        const at = l.findIndex((p) => p[0] === name);
        if (at < 0) l.push([name, value]);
        else { l[at][1] = value; for (let i = l.length - 1; i > at; i--) if (l[i][0] === name) l.splice(i, 1); }
        this.#touch();
      }
      sort() {
        const l = lists.get(this);
        const sorted = l.map((p, i) => [p, i]).sort((a, b) => (a[0][0] < b[0][0] ? -1 : a[0][0] > b[0][0] ? 1 : a[1] - b[1])).map((x) => x[0]);
        l.splice(0, l.length, ...sorted);
        this.#touch();
      }
      forEach(fn, thisArg) { for (const [k, v] of lists.get(this).slice()) fn.call(thisArg, v, k, this); }
      *entries() { const l = lists.get(this); for (let i = 0; i < l.length; i++) yield [l[i][0], l[i][1]]; }
      *keys() { for (const [k] of this.entries()) yield k; }
      *values() { for (const [, v] of this.entries()) yield v; }
      [Symbol.iterator]() { return this.entries(); }
      toString() { return serializeForm(lists.get(this)); }
      get [Symbol.toStringTag]() { return 'URLSearchParams'; }
    }

    // ----- URL
    const records = new WeakMap(); // URL -> its record
    const params = new WeakMap(); // URL -> its URLSearchParams
    const rec_ = (u) => records.get(u);
    class URL {
      constructor(url, base) {
        let b = null;
        if (base !== undefined) { b = parseURL(String(base), null); if (!b) throw new TypeError('Invalid URL'); }
        const r = parseURL(String(url), b);
        if (!r) throw new TypeError('Invalid URL');
        records.set(this, r);
      }
      static canParse(url, base) { try { new URL(url, base); return true; } catch { return false; } }
      static parse(url, base) { try { return new URL(url, base); } catch { return null; } }
      get href() { return serialize(rec_(this)); }
      set href(v) { const r = parseURL(String(v), null); if (!r) throw new TypeError('Invalid URL'); records.set(this, r); this.#sync(); }
      get origin() {
        const r = rec_(this);
        if (r.scheme === 'blob:') { try { const inner = new URL(serializePath(r)); if (inner.protocol === 'http:' || inner.protocol === 'https:') return inner.origin; } catch { /* opaque */ } return 'null'; }
        if (isSpecial(r.scheme) && r.scheme !== 'file:') return r.scheme + '//' + r.host + (r.port !== null ? ':' + r.port : '');
        return 'null';
      }
      get protocol() { return rec_(this).scheme; }
      set protocol(v) {
        const r = rec_(this);
        const s = String(v).replace(/:.*$/s, '').toLowerCase();
        if (!/^[a-z][a-z0-9+.-]*$/.test(s)) return;
        const scheme = s + ':';
        if (isSpecial(scheme) !== isSpecial(r.scheme)) return;
        if (scheme === 'file:' && (r.username || r.password || r.port !== null)) return;
        if (r.scheme === 'file:' && r.host === '') return;
        r.scheme = scheme;
        if (SPECIAL[scheme] === r.port) r.port = null;
      }
      get username() { return rec_(this).username; }
      set username(v) { const r = rec_(this); if (r.host === null || r.host === '' || r.scheme === 'file:') return; r.username = pct(String(v), USERINFO); }
      get password() { return rec_(this).password; }
      set password(v) { const r = rec_(this); if (r.host === null || r.host === '' || r.scheme === 'file:') return; r.password = pct(String(v), USERINFO); }
      get host() { const r = rec_(this); return r.host === null ? '' : r.host + (r.port !== null ? ':' + r.port : ''); }
      set host(v) {
        const r = rec_(this);
        if (r.opaque !== null) return;
        const t = parseURL(r.scheme + '//' + String(v).split(/[/?#\\]/)[0] + '/', null);
        if (t && t.host !== null && t.host !== '') { r.host = t.host; if (/:[0-9]+$/.test(String(v).split(/[/?#\\]/)[0]) || t.port !== null) r.port = t.port; }
      }
      get hostname() { const r = rec_(this); return r.host === null ? '' : r.host; }
      set hostname(v) {
        const r = rec_(this);
        if (r.opaque !== null) return;
        const t = parseURL(r.scheme + '//' + String(v).split(/[/?#:\\]/)[0] + '/', null);
        if (t && t.host !== null && t.host !== '') r.host = t.host;
      }
      get port() { const p = rec_(this).port; return p === null ? '' : String(p); }
      set port(v) {
        const r = rec_(this);
        if (r.host === null || r.host === '' || r.scheme === 'file:') return;
        v = String(v);
        if (v === '') { r.port = null; return; }
        const m = /^[0-9]+/.exec(v);
        if (!m || Number(m[0]) > 65535) return;
        r.port = SPECIAL[r.scheme] === Number(m[0]) ? null : Number(m[0]);
      }
      get pathname() { return serializePath(rec_(this)); }
      set pathname(v) {
        const r = rec_(this);
        if (r.opaque !== null) return;
        r.path = [];
        v = String(v);
        const special = isSpecial(r.scheme);
        if (special ? !/^[/\\]/.test(v) : !v.startsWith('/')) v = '/' + v;
        if (v === '/' && !special && r.host === null) { r.path = ['']; return; }
        pushPath(r, v, special); r.path.shift();
      }
      get search() { const q = rec_(this).query; return q === null || q === '' ? '' : '?' + q; }
      set search(v) {
        const r = rec_(this);
        v = String(v);
        if (v === '') { r.query = null; } else { if (v[0] === '?') v = v.slice(1); r.query = pct(v, isSpecial(r.scheme) ? SPECIAL_QUERY : QUERY); }
        const p = params.get(this);
        if (p) { const l = lists.get(p); l.splice(0, l.length, ...parseForm(r.query || '')); }
      }
      get searchParams() {
        let p = params.get(this);
        if (!p) {
          p = new URLSearchParams(rec_(this).query || '');
          owners.set(p, () => { const s = serializeForm(lists.get(p)); rec_(this).query = s === '' ? null : s; });
          params.set(this, p);
        }
        return p;
      }
      get hash() { const f = rec_(this).fragment; return f === null || f === '' ? '' : '#' + f; }
      set hash(v) { const r = rec_(this); v = String(v); if (v === '') { r.fragment = null; return; } r.fragment = pct(v[0] === '#' ? v.slice(1) : v, FRAGMENT); }
      #sync() { const p = params.get(this); if (p) { const l = lists.get(p); l.splice(0, l.length, ...parseForm(rec_(this).query || '')); } }
      toString() { return this.href; }
      toJSON() { return this.href; }
      get [Symbol.toStringTag]() { return 'URL'; }
    }
    def('URL', URL);
    def('URLSearchParams', URLSearchParams);
  }

  return { bin, unbin, toBytes, utf8enc, utf8dec, toText, fromText, call: hs };
}
