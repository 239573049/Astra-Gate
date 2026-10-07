/**
 * Headers required by mutating /api endpoints. The admin header guards
 * against CSRF from web origins; the runtime token (from runtime.json) is
 * required for POST /api/admin/shutdown.
 */
export function adminHeaders(runtimeToken?: string | null): Record<string, string> {
  const headers: Record<string, string> = { 'X-Astra-Admin': '1' };
  if (runtimeToken != null && runtimeToken !== '') {
    headers['X-Astra-Runtime-Token'] = runtimeToken;
  }
  return headers;
}
