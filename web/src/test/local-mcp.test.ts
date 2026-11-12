import { describe, expect, it } from 'vitest';

import { parseArgs, parseEnv, parseHeaders } from '../components/LocalMcpServers';

describe('local MCP form parsing', () => {
  it('takes one argument per line and drops blank lines', () => {
    expect(parseArgs('-y\n@mcp/files\r\n\n  \n/Users/me/my docs\n')).toEqual(['-y', '@mcp/files', '/Users/me/my docs']);
  });

  it('reads NAME=value lines, keeping "=" inside the value', () => {
    expect(parseEnv('API_KEY=sk-1\nURL=https://x/?a=b\n\n')).toEqual({ API_KEY: 'sk-1', URL: 'https://x/?a=b' });
    expect(parseEnv('')).toEqual({});
  });

  it('rejects a line without a name', () => {
    expect(parseEnv('=oops')).toBeNull();
    expect(parseEnv('no equals sign')).toBeNull();
  });

  it('reads Name: value header lines and trims around the colon', () => {
    expect(parseHeaders('Authorization: Bearer abc\nX-Team:  core \n\n')).toEqual({ Authorization: 'Bearer abc', 'X-Team': 'core' });
    expect(parseHeaders(': nope')).toBeNull();
    expect(parseHeaders('no colon')).toBeNull();
  });
});
