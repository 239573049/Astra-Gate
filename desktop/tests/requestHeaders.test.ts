import { describe, expect, it } from 'vitest';

import { adminHeaders } from '../src/shared/requestHeaders';

describe('adminHeaders', () => {
  it('always sends the admin header', () => {
    expect(adminHeaders()['X-Astra-Admin']).toBe('1');
    expect(adminHeaders(null)['X-Astra-Admin']).toBe('1');
    expect(adminHeaders('')['X-Astra-Admin']).toBe('1');
  });

  it('adds the runtime token when present', () => {
    expect(adminHeaders('tok-123')['X-Astra-Runtime-Token']).toBe('tok-123');
    expect(adminHeaders('tok-123')).not.toHaveProperty('X-Astra-Admin', undefined);
  });

  it('omits the token header when absent', () => {
    expect(adminHeaders(null)).not.toHaveProperty('X-Astra-Runtime-Token');
    expect(Object.keys(adminHeaders())).toEqual(['X-Astra-Admin']);
  });
});
