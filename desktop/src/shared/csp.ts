/**
 * Strict CSP for pages served from app://astra. The web app is a static
 * Vite build, so scripts/styles come from 'self'; API calls go to the local
 * server origin (http://127.0.0.1:<port>). Images may also come from https:
 * plugin and market icons are https URLs (the server's PluginService.SafeIcon
 * admits nothing else), and the web build shows them from wherever they live.
 */
export function buildCsp(apiOrigin: string): string {
  return [
    "default-src 'self'",
    "script-src 'self'",
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob: https:",
    "font-src 'self' data:",
    "connect-src 'self' " + apiOrigin,
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'none'",
    "frame-ancestors 'none'",
  ].join('; ');
}
