import { describe, expect, it } from 'vitest';

import { buildCsp } from '../src/shared/csp';

describe('buildCsp', () => {
  it('locks everything to self and allows the api origin in connect-src', () => {
    const csp = buildCsp('http://127.0.0.1:17321');
    expect(csp).toContain("default-src 'self'");
    expect(csp).toContain("script-src 'self'");
    expect(csp).toContain("connect-src 'self' http://127.0.0.1:17321");
    expect(csp).toContain("object-src 'none'");
    expect(csp).toContain("frame-ancestors 'none'");
  });

  it('allows https images for plugin icons, but not plain http', () => {
    const csp = buildCsp('http://127.0.0.1:17321');
    expect(csp).toContain("img-src 'self' data: blob: https:;");
    expect(csp).not.toMatch(/img-src[^;]*http:/);
  });

  it('varies with the port', () => {
    expect(buildCsp('http://127.0.0.1:18000')).toContain('http://127.0.0.1:18000');
  });
});
